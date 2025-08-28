using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Binance.Net.Clients;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Sockets;
using Models;
using Config;

namespace Services
{
    public class MultiSymbolWebSocketService
    {
        private readonly BinanceSocketClient _socketClient;
        private readonly DataStorageService _dataStorage;
        private readonly BackendConfig _config;
        
        private readonly ConcurrentDictionary<string, UpdateSubscription> _priceSubscriptions = new();
        private readonly ConcurrentDictionary<string, UpdateSubscription> _candleSubscriptions = new();
        
        private bool _isRunning = false;
        private DateTime _startTime;

        // События для уведомлений
        public event Action<string, decimal>? OnPriceUpdate;
        public event Action<string, CandleData>? OnCandleUpdate;
        public event Action<string, decimal?>? OnNatrUpdate;
        
        public MultiSymbolWebSocketService(
            BinanceSocketClient socketClient, 
            DataStorageService dataStorage, 
            BackendConfig config)
        {
            _socketClient = socketClient;
            _dataStorage = dataStorage;
            _config = config;
        }

        /// <summary>
        /// Запуск WebSocket подключений для списка символов
        /// </summary>
        public async Task<bool> StartAsync(List<string> symbols)
        {
            if (_isRunning)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ WebSocket сервис уже запущен");
                return false;
            }

            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 Запуск WebSocket для {symbols.Count} символов...");
                _startTime = DateTime.UtcNow;

                // Подписываемся на обновления цен (тикеры)
                var priceResult = await SubscribeToPricesAsync(symbols);
                if (!priceResult)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка подписки на цены");
                    return false;
                }

                // Подписываемся на свечи 1m
                var candleResult = await SubscribeToCandlesAsync(symbols);
                if (!candleResult)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка подписки на свечи");
                    await StopAsync(); // Останавливаем уже созданные подписки
                    return false;
                }

                _isRunning = true;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ WebSocket запущен для {symbols.Count} символов");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Активных подписок: цены={_priceSubscriptions.Count}, свечи={_candleSubscriptions.Count}");
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка запуска WebSocket: {ex.Message}");
                await StopAsync();
                return false;
            }
        }

        /// <summary>
        /// Подписка на обновления цен
        /// </summary>
        private async Task<bool> SubscribeToPricesAsync(List<string> symbols)
        {
            try
            {
                // Подписываемся пакетами для избежания лимитов
                var batchSize = 20;
                var successCount = 0;

                for (int i = 0; i < symbols.Count; i += batchSize)
                {
                    var batch = symbols.Skip(i).Take(batchSize).ToList();
                    
                    var subscription = await _socketClient.UsdFuturesApi.SubscribeToTickerUpdatesAsync(
                        batch,
                        (update) =>
                        {
                            var symbol = update.Data.Symbol;
                            var price = update.Data.LastPrice;
                            
                            // Обновляем цену в хранилище
                            var coinData = _dataStorage.GetCoinData(symbol);
                            if (coinData != null)
                            {
                                coinData.CurrentPrice = price;
                                coinData.LastUpdated = DateTime.UtcNow;
                                _dataStorage.UpdateCoinData(symbol, coinData);
                            }

                            // Уведомляем подписчиков
                            OnPriceUpdate?.Invoke(symbol, price);
                        });

                    if (subscription.Success)
                    {
                        foreach (var symbol in batch)
                        {
                            _priceSubscriptions[symbol] = subscription.Data;
                        }
                        successCount += batch.Count;
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка подписки на цены для пакета: {subscription.Error}");
                    }

                    // Небольшая задержка между пакетами
                    await Task.Delay(100);
                }

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📈 Подписка на цены: {successCount}/{symbols.Count} символов");
                return successCount > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка подписки на цены: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Подписка на обновления свечей
        /// </summary>
        private async Task<bool> SubscribeToCandlesAsync(List<string> symbols)
        {
            try
            {
                var batchSize = 20;
                var successCount = 0;

                for (int i = 0; i < symbols.Count; i += batchSize)
                {
                    var batch = symbols.Skip(i).Take(batchSize).ToList();
                    
                    var subscription = await _socketClient.UsdFuturesApi.SubscribeToKlineUpdatesAsync(
                        batch,
                        Binance.Net.Enums.KlineInterval.OneMinute,
                        (update) =>
                        {
                            var klineData = update.Data.Data;
                            
                            // Обрабатываем только закрытые свечи
                            if (!klineData.Final)
                                return;

                            var symbol = update.Data.Symbol; // Символ находится в update.Data.Symbol, а не в klineData
                            var newCandle = new CandleData
                            {
                                OpenTime = klineData.OpenTime,
                                Open = klineData.OpenPrice,
                                High = klineData.HighPrice,
                                Low = klineData.LowPrice,
                                Close = klineData.ClosePrice,
                                Volume = klineData.Volume
                            };

                            // Асинхронная обработка для снижения лагов
                            Task.Run(() =>
                            {
                                try
                                {
                                    var coinData = _dataStorage.GetCoinData(symbol);
                                    if (coinData != null)
                                    {
                                        // Добавляем новую свечу и пересчитываем NATR
                                        TechnicalAnalysisService.UpdateCandleData(coinData, newCandle, _config.HistoryCandles + 10);
                                        _dataStorage.UpdateCoinData(symbol, coinData);

                                        // Уведомляем подписчиков
                                        OnCandleUpdate?.Invoke(symbol, newCandle);
                                        OnNatrUpdate?.Invoke(symbol, coinData.Natr);

                                        // Убран мусорный лог новых свечей
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка обработки свечи {symbol}: {ex.Message}");
                                }
                            });
                        });

                    if (subscription.Success)
                    {
                        foreach (var symbol in batch)
                        {
                            _candleSubscriptions[symbol] = subscription.Data;
                        }
                        successCount += batch.Count;
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка подписки на свечи для пакета: {subscription.Error}");
                    }

                    await Task.Delay(100);
                }

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🕐 Подписка на свечи: {successCount}/{symbols.Count} символов");
                return successCount > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка подписки на свечи: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Остановка всех WebSocket подключений
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning)
                return;

            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 Остановка WebSocket сервиса...");

                // Отписываемся от всех подписок
                var unsubscribeTasks = new List<Task>();

                foreach (var subscription in _priceSubscriptions.Values)
                {
                    unsubscribeTasks.Add(subscription.CloseAsync());
                }

                foreach (var subscription in _candleSubscriptions.Values)
                {
                    unsubscribeTasks.Add(subscription.CloseAsync());
                }

                await Task.WhenAll(unsubscribeTasks);

                _priceSubscriptions.Clear();
                _candleSubscriptions.Clear();
                _isRunning = false;

                var uptime = DateTime.UtcNow - _startTime;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ WebSocket остановлен. Время работы: {uptime.TotalMinutes:F1} мин");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка остановки WebSocket: {ex.Message}");
            }
        }

        /// <summary>
        /// Получение статистики WebSocket соединений
        /// </summary>
        public WebSocketStats GetStats()
        {
            return new WebSocketStats
            {
                IsRunning = _isRunning,
                StartTime = _isRunning ? _startTime : (DateTime?)null,
                Uptime = _isRunning ? DateTime.UtcNow - _startTime : TimeSpan.Zero,
                PriceSubscriptions = _priceSubscriptions.Count,
                CandleSubscriptions = _candleSubscriptions.Count,
                TotalSubscriptions = _priceSubscriptions.Count + _candleSubscriptions.Count
            };
        }

        /// <summary>
        /// Добавление новых символов к существующим подпискам
        /// </summary>
        public async Task<bool> AddSymbolsAsync(List<string> newSymbols)
        {
            if (!_isRunning)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ WebSocket не запущен, невозможно добавить символы");
                return false;
            }

            // Фильтруем только новые символы
            var symbolsToAdd = newSymbols.Where(s => !_priceSubscriptions.ContainsKey(s)).ToList();
            
            if (symbolsToAdd.Count == 0)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ℹ️ Все символы уже отслеживаются");
                return true;
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ➕ Добавляем {symbolsToAdd.Count} новых символов к WebSocket...");

            var priceResult = await SubscribeToPricesAsync(symbolsToAdd);
            var candleResult = await SubscribeToCandlesAsync(symbolsToAdd);

            return priceResult && candleResult;
        }
    }

    public class WebSocketStats
    {
        public bool IsRunning { get; set; }
        public DateTime? StartTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public int PriceSubscriptions { get; set; }
        public int CandleSubscriptions { get; set; }
        public int TotalSubscriptions { get; set; }
    }
}
