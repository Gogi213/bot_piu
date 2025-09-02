using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Sockets;
using Models;
using Config;

namespace Services
{
    /// <summary>
    /// Сервис для создания 15-секундных свечей из AggregateTrade
    /// </summary>
    public class FifteenSecondCandleService
    {
        private readonly BinanceSocketClient _socketClient;
        private readonly DataStorageService _dataStorage;
        private readonly BackendConfig _config;
        
        // Хранение данных для построения свечей
        private readonly ConcurrentDictionary<string, UpdateSubscription> _aggTradeSubscriptions = new();
        private readonly ConcurrentDictionary<string, FifteenSecondCandleBuilder> _candleBuilders = new();
        private readonly ConcurrentDictionary<string, List<CandleData>> _fifteenSecondCandles = new();
        
        private readonly Timer? _candleTimer;
        private volatile bool _isRunning = false;

        // События
        public event Action<string, CandleData>? OnFifteenSecondCandle;
        public event Action<string>? OnWarmupCompleted;

        public FifteenSecondCandleService(
            BinanceSocketClient socketClient,
            DataStorageService dataStorage,
            BackendConfig config)
        {
            _socketClient = socketClient;
            _dataStorage = dataStorage;
            _config = config;
            
            // Таймер для точной отбивки каждые 15 секунд
            _candleTimer = new Timer(FlushCandles, null, GetNextFifteenSecondInterval(), TimeSpan.FromSeconds(15));
        }

        /// <summary>
        /// Запуск подписки на AggregateTrade для символов
        /// </summary>
        public async Task<bool> StartAsync(List<string> symbols)
        {
            if (_isRunning)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ 15s Candle Service уже запущен");
                return false;
            }

            if (!_config.EnableFifteenSecondTrading)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ℹ️ 15-секундная торговля отключена в конфиге");
                return false;
            }

            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔥 Запуск 15s свечей для {symbols.Count} символов...");
                
                var batchSize = 20;
                var successCount = 0;

                for (int i = 0; i < symbols.Count; i += batchSize)
                {
                    var batch = symbols.Skip(i).Take(batchSize).ToList();
                    
                    var subscription = await _socketClient.UsdFuturesApi.SubscribeToAggregatedTradeUpdatesAsync(
                        batch,
                        (update) =>
                        {
                            var symbol = update.Data.Symbol;
                            var price = update.Data.Price;
                            var quantity = update.Data.Quantity;
                            var timestamp = update.Data.TradeTime;

                            // Получаем или создаем builder для символа
                            var builder = _candleBuilders.GetOrAdd(symbol, _ => new FifteenSecondCandleBuilder());
                            
                            // Добавляем трейд
                            builder.AddTrade(price, quantity, timestamp);
                        });

                    if (subscription.Success)
                    {
                        foreach (var symbol in batch)
                        {
                            _aggTradeSubscriptions[symbol] = subscription.Data;
                            _fifteenSecondCandles[symbol] = new List<CandleData>();
                        }
                        successCount += batch.Count;
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка подписки на AggTrades: {subscription.Error}");
                    }

                    await Task.Delay(100);
                }

                _isRunning = true;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ 15s свечи запущены для {successCount}/{symbols.Count} символов");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🕐 Прогрев: ждём {_config.FifteenSecondWarmupCandles} свечей");
                
                return successCount > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка запуска 15s сервиса: {ex.Message}");
                await StopAsync();
                return false;
            }
        }

        /// <summary>
        /// Точная отбивка каждые 15 секунд
        /// </summary>
        private void FlushCandles(object? state)
        {
            if (!_isRunning) return;

            var currentTime = DateTime.UtcNow;
            var flushTime = GetCurrentFifteenSecondMark(currentTime);

            foreach (var kvp in _candleBuilders)
            {
                var symbol = kvp.Key;
                var builder = kvp.Value;

                var candle = builder.BuildCandle(flushTime);
                if (candle != null)
                {
                    // Добавляем свечу в коллекцию
                    var candles = _fifteenSecondCandles[symbol];
                    candles.Add(candle);
                    
                    // Ограничиваем количество свечей
                    while (candles.Count > _config.FifteenSecondWarmupCandles + 10)
                    {
                        candles.RemoveAt(0);
                    }

                    // Проверяем прогрев
                    if (candles.Count >= _config.FifteenSecondWarmupCandles)
                    {
                        OnWarmupCompleted?.Invoke(symbol);
                    }

                    // Уведомляем подписчиков
                    OnFifteenSecondCandle?.Invoke(symbol, candle);

                    // Убрали лог цен - слишком много шума
                }
            }
        }

        /// <summary>
        /// Получить 15-секундные свечи для символа
        /// </summary>
        public List<CandleData>? GetFifteenSecondCandles(string symbol)
        {
            return _fifteenSecondCandles.TryGetValue(symbol, out var candles) ? candles : null;
        }

        /// <summary>
        /// Проверить готовность символа (прогрет ли)
        /// </summary>
        public bool IsSymbolReady(string symbol)
        {
            return _fifteenSecondCandles.TryGetValue(symbol, out var candles) && 
                   candles.Count >= _config.FifteenSecondWarmupCandles;
        }

        /// <summary>
        /// Удаление конкретных символов из 15s мониторинга
        /// </summary>
        public async Task RemoveSymbolsAsync(List<string> symbolsToRemove)
        {
            if (!_isRunning || symbolsToRemove.Count == 0) return;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚫 Удаление 15s символов: {symbolsToRemove.Count} монет");

            foreach (var symbol in symbolsToRemove)
            {
                if (_aggTradeSubscriptions.TryRemove(symbol, out var subscription))
                {
                    try
                    {
                        await subscription.CloseAsync();
                        _candleBuilders.TryRemove(symbol, out _);
                        _fifteenSecondCandles.TryRemove(symbol, out _);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ➖ Удален 15s: {symbol} (исключен из пула)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка закрытия подписки {symbol}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Умное обновление списка символов - сохраняем прогретые данные
        /// </summary>
        public async Task UpdateSymbolsAsync(List<string> newSymbols)
        {
            if (!_isRunning) return;

            var currentSymbols = _aggTradeSubscriptions.Keys.ToHashSet();
            var newSymbolsSet = newSymbols.ToHashSet();

            // Находим символы для добавления и удаления
            var toAdd = newSymbolsSet.Except(currentSymbols).ToList();
            var toRemove = currentSymbols.Except(newSymbolsSet).ToList();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔄 15s обновление: +{toAdd.Count} -{toRemove.Count} ={newSymbolsSet.Count} монет");

            // Удаляем старые символы
            foreach (var symbol in toRemove)
            {
                if (_aggTradeSubscriptions.TryRemove(symbol, out var subscription))
                {
                    try
                    {
                        await subscription.CloseAsync();
                        _candleBuilders.TryRemove(symbol, out _);
                        _fifteenSecondCandles.TryRemove(symbol, out _);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Удален 15s: {symbol}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка удаления {symbol}: {ex.Message}");
                    }
                }
            }

            // Добавляем новые символы
            if (toAdd.Count > 0)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ➕ Добавляем 15s для {toAdd.Count} новых монет");
                
                // Подписываемся на новые символы пакетами по 10
                const int batchSize = 10;
                int addedCount = 0;

                for (int i = 0; i < toAdd.Count; i += batchSize)
                {
                    var batch = toAdd.Skip(i).Take(batchSize).ToList();
                    
                    var subscription = await _socketClient.UsdFuturesApi.SubscribeToAggregatedTradeUpdatesAsync(
                        batch,
                        update =>
                        {
                            var symbol = update.Data.Symbol;
                            var price = update.Data.Price;
                            var quantity = update.Data.Quantity;
                            var timestamp = update.Data.TradeTime;

                            var builder = _candleBuilders.GetOrAdd(symbol, _ => new FifteenSecondCandleBuilder());
                            builder.AddTrade(price, quantity, timestamp);
                        });

                    if (subscription.Success)
                    {
                        foreach (var symbol in batch)
                        {
                            _aggTradeSubscriptions[symbol] = subscription.Data;
                            _fifteenSecondCandles[symbol] = new List<CandleData>();
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Добавлен 15s: {symbol} (прогрев с нуля)");
                        }
                        addedCount += batch.Count;
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка добавления 15s пакета: {subscription.Error}");
                    }

                    await Task.Delay(100);
                }

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Добавлено 15s: {addedCount}/{toAdd.Count} символов");
            }

            // Сохраненные символы продолжают работать с прогретыми данными
            var preservedCount = currentSymbols.Intersect(newSymbolsSet).Count();
            if (preservedCount > 0)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💎 Сохранено прогретых 15s: {preservedCount} монет");
            }
        }

        /// <summary>
        /// Остановка сервиса
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _candleTimer?.Dispose();

            foreach (var subscription in _aggTradeSubscriptions.Values)
            {
                try
                {
                    await subscription.CloseAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка закрытия подписки: {ex.Message}");
                }
            }

            _aggTradeSubscriptions.Clear();
            _candleBuilders.Clear();
            _fifteenSecondCandles.Clear();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔥 15s Candle Service остановлен");
        }

        #region Helpers

        /// <summary>
        /// Получить следующую 15-секундную отметку
        /// </summary>
        private TimeSpan GetNextFifteenSecondInterval()
        {
            var now = DateTime.UtcNow;
            var nextMark = GetCurrentFifteenSecondMark(now).AddSeconds(15);
            return nextMark - now;
        }

        /// <summary>
        /// Получить текущую 15-секундную отметку
        /// </summary>
        private DateTime GetCurrentFifteenSecondMark(DateTime time)
        {
            var seconds = time.Second;
            var aligned = (seconds / 15) * 15;
            return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, aligned, DateTimeKind.Utc);
        }

        #endregion
    }

    /// <summary>
    /// Класс для построения 15-секундных свечей из трейдов
    /// </summary>
    public class FifteenSecondCandleBuilder
    {
        private decimal? _open;
        private decimal _high = decimal.MinValue;
        private decimal _low = decimal.MaxValue;
        private decimal _close;
        private decimal _volume = 0;
        private DateTime _openTime;
        private readonly object _lock = new object();

        public void AddTrade(decimal price, decimal quantity, DateTime tradeTime)
        {
            lock (_lock)
            {
                // Первый трейд в интервале
                if (_open == null)
                {
                    _open = price;
                    _openTime = tradeTime;
                }

                // Обновляем High/Low/Close/Volume
                if (price > _high) _high = price;
                if (price < _low) _low = price;
                _close = price;
                _volume += quantity;
            }
        }

        public CandleData? BuildCandle(DateTime closeTime)
        {
            lock (_lock)
            {
                if (_open == null) return null;

                var candle = new CandleData
                {
                    OpenTime = _openTime,
                    Open = _open.Value,
                    High = _high,
                    Low = _low,
                    Close = _close,
                    Volume = _volume
                };

                // Сброс для следующего интервала
                _open = null;
                _high = decimal.MinValue;
                _low = decimal.MaxValue;
                _close = 0;
                _volume = 0;

                return candle;
            }
        }
    }
}
