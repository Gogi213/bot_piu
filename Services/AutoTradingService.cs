using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Binance.Net.Clients;
using Models;
using Config;
using Trading;

namespace Services
{
    /// <summary>
    /// Автоматический торговый сервис, объединяющий HFT анализ с торговым модулем
    /// </summary>
    public class AutoTradingService
    {
        private readonly HftSignalEngineService _hftEngine;
        private readonly DataStorageService _dataStorage;
        private readonly UniverseUpdateService _universeService;
        private readonly MultiSymbolWebSocketService _webSocketService;
        private readonly TradingStrategyService _strategyService;
        private readonly BackendConfig _backendConfig;
        private readonly TradingConfig _tradingConfig;
        private readonly AutoTradingConfig _autoTradingConfig;
        private readonly BinanceRestClient _restClient;
        private readonly BinanceSocketClient _socketClient;
        private readonly SimpleStateManager _stateManager;

        // Торговые модули для активных позиций
        private readonly ConcurrentDictionary<string, TradingModule> _activeTradingModules = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();
        private readonly ConcurrentDictionary<string, string> _lastSignal = new();
        private readonly ConcurrentDictionary<string, SimpleStateManager.ActivePosition> _activePositions = new();

        private Timer? _universeUpdateTimer;
        private volatile bool _isRunning = false;
        private DateTime _startTime;

        // События
        public event Action<string, string, StrategyResult>? OnSignalReceived;
        public event Action<string, string>? OnTradeOpened;
        public event Action<string, string>? OnTradeClosed;
        public event Action<string>? OnError;

        public AutoTradingService(
            HftSignalEngineService hftEngine,
            DataStorageService dataStorage,
            UniverseUpdateService universeService,
            MultiSymbolWebSocketService webSocketService,
            TradingStrategyService strategyService,
            BackendConfig backendConfig,
            TradingConfig tradingConfig,
            AutoTradingConfig autoTradingConfig,
            BinanceRestClient restClient,
            BinanceSocketClient socketClient,
            SimpleStateManager stateManager)
        {
            _hftEngine = hftEngine;
            _dataStorage = dataStorage;
            _universeService = universeService;
            _webSocketService = webSocketService;
            _strategyService = strategyService;
            _backendConfig = backendConfig;
            _tradingConfig = tradingConfig;
            _autoTradingConfig = autoTradingConfig;
            _restClient = restClient;
            _socketClient = socketClient;
            _stateManager = stateManager;
        }

        /// <summary>
        /// Запуск автоматической торговой системы
        /// </summary>
        public async Task<bool> StartAsync()
        {
            if (_isRunning)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Автоматическая торговля уже запущена");
                return false;
            }

            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 ЗАПУСК АВТОМАТИЧЕСКОЙ ТОРГОВОЙ СИСТЕМЫ");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ===============================================");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚡ HFT анализ: каждые 100мс");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 Максимум позиций: {_autoTradingConfig.MaxConcurrentPositions}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💰 Риск на сделку: {_autoTradingConfig.RiskPercentPerTrade}%");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⏰ Пауза между сделками: {_autoTradingConfig.MinTimeBetweenTradesMinutes} мин");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚡ Минимальная сила сигнала: {_autoTradingConfig.MinSignalStrength}");
                Console.WriteLine();

                _startTime = DateTime.UtcNow;
                _isRunning = true;

                // Восстановление состояния из базы данных
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💾 Восстановление состояния...");
                await RestoreStateAsync();

                // Первичная загрузка данных
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Первичная загрузка данных...");
                var universeResult = await _universeService.UpdateUniverseAsync();
                if (!universeResult.Success)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка загрузки данных: {universeResult.ErrorMessage}");
                    return false;
                }

                var filteredCoins = _dataStorage.GetFilteredCoins(_backendConfig.MinVolumeUsdt, _backendConfig.MinNatrPercent);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Подготовлено {filteredCoins.Count} монет для торговли");

                // Запуск WebSocket для мониторинга цен
                var symbols = filteredCoins.Take(20).Select(c => c.Symbol).ToList(); // Ограничиваем до 20 для стабильности
                await _webSocketService.StartAsync(symbols);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📡 WebSocket запущен для {symbols.Count} символов");

                // Интеграция WebSocket с HFT движком
                _webSocketService.OnPriceUpdate += (symbol, price) =>
                {
                    _hftEngine.UpdatePrice(symbol, price);
                };

                // Подписка на сигналы HFT движка
                _hftEngine.OnHftSignalChange += OnHftSignalChangeHandler;

                // Запуск HFT движка
                var hftStarted = await _hftEngine.StartAsync();
                if (!hftStarted)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка запуска HFT движка");
                    return false;
                }

                // Таймер для периодического обновления вселенной
                var updateIntervalMs = _backendConfig.UpdateIntervalMinutes * 60 * 1000;
                _universeUpdateTimer = new Timer(async _ => await UpdateUniverseAsync(), 
                                               null, updateIntervalMs, updateIntervalMs);

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Автоматическая торговая система запущена");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 Ожидание торговых сигналов...");
                Console.WriteLine();

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка запуска автоматической торговли: {ex.Message}");
                _isRunning = false;
                OnError?.Invoke($"Ошибка запуска: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Обработчик изменений сигналов от HFT движка
        /// </summary>
        private async void OnHftSignalChangeHandler(HftSignalEvent hftEvent)
        {
            try
            {
                if (!_isRunning || hftEvent.NewSignal == "FLAT") return;

                var symbol = hftEvent.Symbol;
                var newSignal = hftEvent.NewSignal;

                // Проверяем, изменился ли сигнал
                if (_lastSignal.TryGetValue(symbol, out var lastSignal) && lastSignal == newSignal)
                    return;

                _lastSignal[symbol] = newSignal;

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 ТОРГОВЫЙ СИГНАЛ: {symbol} → {GetSignalEmoji(newSignal)}{newSignal}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    💰 Цена: {hftEvent.Price:F6}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    📊 Z-Score: {hftEvent.ZScore:F2}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    ⚡ Задержка: {hftEvent.LatencyMs}мс");

                // Получаем полную информацию о стратегии
                var coinData = _dataStorage.GetCoinData(symbol);
                if (coinData == null) return;

                var strategyResult = _strategyService.AnalyzeCoin(coinData);
                OnSignalReceived?.Invoke(symbol, newSignal, strategyResult);

                // Проверяем возможность открытия сделки
                if (await CanOpenTradeAsync(symbol, newSignal, strategyResult))
                {
                    await OpenTradeAsync(symbol, newSignal, strategyResult);
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⏸️ Сделка пропущена: {GetTradeBlockReason(symbol)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка обработки сигнала {hftEvent.Symbol}: {ex.Message}");
                OnError?.Invoke($"Ошибка сигнала {hftEvent.Symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Проверка возможности открытия сделки
        /// </summary>
        private async Task<bool> CanOpenTradeAsync(string symbol, string signal, StrategyResult strategyResult)
        {
            // Проверка 0: Автоторговля включена
            if (!_autoTradingConfig.EnableAutoTrading)
            {
                return false;
            }

            // Проверка 1: Максимум одновременных позиций
            if (_activeTradingModules.Count >= _autoTradingConfig.MaxConcurrentPositions)
            {
                return false;
            }

            // Проверка 2: Уже есть активная позиция по этому символу
            if (_activeTradingModules.ContainsKey(symbol))
            {
                return false;
            }

            // Проверка 3: Минимальное время между сделками
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime))
            {
                var minTime = TimeSpan.FromMinutes(_autoTradingConfig.MinTimeBetweenTradesMinutes);
                if (DateTime.UtcNow - lastTime < minTime)
                {
                    return false;
                }
            }

            // Проверка 4: Сила сигнала
            var signalStrength = Math.Abs(strategyResult.ZScore);
            if (signalStrength < _autoTradingConfig.MinSignalStrength)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Открытие торговой позиции
        /// </summary>
        private async Task OpenTradeAsync(string symbol, string signal, StrategyResult strategyResult)
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 ОТКРЫТИЕ ПОЗИЦИИ: {symbol} {signal}");

                // Создаем конфигурацию для торгового модуля
                var tradingConfig = CreateTradingConfig(symbol, signal, strategyResult);
                
                // Создаем торговый модуль
                var tradingModule = new TradingModule(_restClient, _socketClient, tradingConfig);

                // Запускаем торговлю (TradingModule выполняется автономно)
                var tradingTask = Task.Run(async () =>
                {
                    try
                    {
                        await tradingModule.ExecuteTradeAsync();
                        OnTradeCompletedHandler(symbol, "Completed");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка торговли {symbol}: {ex.Message}");
                        OnTradeCompletedHandler(symbol, $"Error: {ex.Message}");
                    }
                });

                // Сохраняем активный модуль и состояние
                _activeTradingModules[symbol] = tradingModule;
                _lastTradeTime[symbol] = DateTime.UtcNow;

                // Получаем текущую цену для сохранения
                var currentPrice = GetCurrentPrice(symbol);
                await SaveActivePositionAsync(symbol, signal, tradingConfig, currentPrice);

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Позиция открыта: {symbol} {signal}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    💰 Сумма: {tradingConfig.UsdAmount} USDT");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    🎯 Take Profit: {tradingConfig.TakeProfitPercent}%");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    🛡️ Stop Loss: {tradingConfig.StopLossPercent}%");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    📊 Активных позиций: {_activeTradingModules.Count}/{_autoTradingConfig.MaxConcurrentPositions}");
                Console.WriteLine();

                OnTradeOpened?.Invoke(symbol, signal);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка открытия позиции {symbol}: {ex.Message}");
                OnError?.Invoke($"Ошибка открытия {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработчик завершения торговли
        /// </summary>
        private async void OnTradeCompletedHandler(string symbol, string result)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🏁 ПОЗИЦИЯ ЗАКРЫТА: {symbol} - {result}");
            
            // Сохраняем в историю если есть информация о позиции
            if (_activePositions.TryGetValue(symbol, out var position))
            {
                var tradeHistory = new SimpleStateManager.TradeHistoryRecord
                {
                    Symbol = symbol,
                    Side = position.Side,
                    UsdAmount = position.UsdAmount,
                    EntryPrice = position.EntryPrice,
                    Result = result,
                    CreatedAt = position.CreatedAt,
                    ClosedAt = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - position.CreatedAt
                };

                await _stateManager.SaveTradeHistoryAsync(tradeHistory);
                await _stateManager.RemoveActivePositionAsync(symbol);
                _activePositions.TryRemove(symbol, out _);
            }
            
            // Удаляем из активных модулей (TradingModule завершился автономно)
            _activeTradingModules.TryRemove(symbol, out _);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Активных позиций: {_activeTradingModules.Count}/{_autoTradingConfig.MaxConcurrentPositions}");
            OnTradeClosed?.Invoke(symbol, result);
        }

        /// <summary>
        /// Создание конфигурации для торгового модуля
        /// </summary>
        private TradingConfig CreateTradingConfig(string symbol, string signal, StrategyResult strategyResult)
        {
            var side = signal == "LONG" ? "BUY" : "SELL";
            
            return new TradingConfig
            {
                Symbol = symbol,
                Side = side,
                UsdAmount = CalculateTradeAmount(strategyResult),
                TakeProfitPercent = _tradingConfig.TakeProfitPercent,
                StopLossPercent = _tradingConfig.StopLossPercent,
                EnableBreakEven = _tradingConfig.EnableBreakEven,
                BreakEvenActivationPercent = _tradingConfig.BreakEvenActivationPercent,
                BreakEvenStopLossPercent = _tradingConfig.BreakEvenStopLossPercent,
                TickSize = _tradingConfig.TickSize,
                MonitorIntervalSeconds = _tradingConfig.MonitorIntervalSeconds
            };
        }

        /// <summary>
        /// Расчет размера позиции на основе риска
        /// </summary>
        private decimal CalculateTradeAmount(StrategyResult strategyResult)
        {
            // Базовая сумма из конфигурации
            var baseAmount = _tradingConfig.UsdAmount;
            
            // Корректировка на основе силы сигнала (Z-Score)
            var signalStrength = Math.Abs(strategyResult.ZScore);
            var multiplier = Math.Min(2.0m, Math.Max(0.8m, signalStrength / _autoTradingConfig.MinSignalStrength)); // От 0.8x до 2x
            
            return Math.Round(baseAmount * multiplier, 2);
        }

        /// <summary>
        /// Причина блокировки сделки
        /// </summary>
        private string GetTradeBlockReason(string symbol)
        {
            if (!_autoTradingConfig.EnableAutoTrading)
                return "Автоторговля отключена";
            
            if (_activeTradingModules.Count >= _autoTradingConfig.MaxConcurrentPositions)
                return $"Максимум позиций ({_autoTradingConfig.MaxConcurrentPositions})";
            
            if (_activeTradingModules.ContainsKey(symbol))
                return "Позиция уже открыта";
            
            if (_lastTradeTime.TryGetValue(symbol, out var lastTime))
            {
                var minTime = TimeSpan.FromMinutes(_autoTradingConfig.MinTimeBetweenTradesMinutes);
                var timeSince = DateTime.UtcNow - lastTime;
                if (timeSince < minTime)
                {
                    var remaining = minTime - timeSince;
                    return $"Пауза еще {remaining.TotalMinutes:F0} мин";
                }
            }
            
            return "Слабый сигнал или неизвестная причина";
        }

        /// <summary>
        /// Восстановление состояния после перезапуска
        /// </summary>
        private async Task RestoreStateAsync()
        {
            try
            {
                // Восстанавливаем активные позиции
                var activePositions = await _stateManager.LoadActivePositionsAsync();
                foreach (var kvp in activePositions)
                {
                    _activePositions[kvp.Key] = kvp.Value;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💾 Восстановлена позиция: {kvp.Value.Symbol} {kvp.Value.Side} ({kvp.Value.UsdAmount} USDT)");
                }

                // Восстанавливаем торговое состояние
                var tradingState = await _stateManager.LoadTradingStateAsync();
                foreach (var kvp in tradingState)
                {
                    _lastTradeTime[kvp.Key] = kvp.Value;
                }

                if (activePositions.Count > 0 || tradingState.Count > 0)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Состояние восстановлено: {activePositions.Count} позиций, {tradingState.Count} торговых состояний");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка восстановления состояния: {ex.Message}");
                await _stateManager.LogSystemEventAsync("STATE_RESTORE_ERROR", ex.Message, ex.StackTrace);
            }
        }

        /// <summary>
        /// Сохранение активной позиции
        /// </summary>
        private async Task SaveActivePositionAsync(string symbol, string signal, TradingConfig tradingConfig, decimal entryPrice)
        {
            try
            {
                var position = new SimpleStateManager.ActivePosition
                {
                    Symbol = symbol,
                    Side = signal == "LONG" ? "BUY" : "SELL",
                    UsdAmount = tradingConfig.UsdAmount,
                    EntryPrice = entryPrice,
                    TradingConfig = tradingConfig,
                    CreatedAt = DateTime.UtcNow
                };

                await _stateManager.SaveActivePositionAsync(position);
                _activePositions[symbol] = position;
                
                // Сохраняем время последней сделки
                await _stateManager.SaveTradingStateAsync(symbol, DateTime.UtcNow, signal);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка сохранения позиции {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Получение текущей цены символа из кеша
        /// </summary>
        private decimal GetCurrentPrice(string symbol)
        {
            var coinData = _dataStorage.GetCoinData(symbol);
            return coinData?.CurrentPrice ?? 0;
        }

        /// <summary>
        /// Периодическое обновление вселенной монет
        /// </summary>
        private async Task UpdateUniverseAsync()
        {
            if (!_isRunning) return;

            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔄 Обновление пула монет...");
                
                var result = await _universeService.UpdateUniverseAsync();
                if (result.Success)
                {
                    var filteredCoins = _dataStorage.GetFilteredCoins(_backendConfig.MinVolumeUsdt, _backendConfig.MinNatrPercent);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Пул обновлен: {filteredCoins.Count} монет");
                    
                    // Обновляем WebSocket подписки если нужно
                    var newSymbols = filteredCoins.Take(20).Select(c => c.Symbol).ToList();
                    // TODO: Реализовать умное обновление WebSocket подписок
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка обновления пула: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка обновления вселенной: {ex.Message}");
                OnError?.Invoke($"Ошибка обновления: {ex.Message}");
            }
        }

        /// <summary>
        /// Остановка автоматической торговли
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 Остановка автоматической торговли...");
            _isRunning = false;

            // Останавливаем таймер
            _universeUpdateTimer?.Dispose();

            // Останавливаем HFT движок
            await _hftEngine.StopAsync();

            // Останавливаем WebSocket
            await _webSocketService.StopAsync();

            // Очищаем активные модули (они завершатся автономно)
            _activeTradingModules.Clear();

            var uptime = DateTime.UtcNow - _startTime;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Автоматическая торговля остановлена");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⏰ Время работы: {uptime.TotalHours:F1} часов");
        }

        /// <summary>
        /// Получение статистики торговли
        /// </summary>
        public AutoTradingStats GetStats()
        {
            var uptime = _isRunning ? DateTime.UtcNow - _startTime : TimeSpan.Zero;
            
            return new AutoTradingStats
            {
                IsRunning = _isRunning,
                Uptime = uptime,
                ActivePositions = _activeTradingModules.Count,
                MaxPositions = _autoTradingConfig.MaxConcurrentPositions,
                TotalSymbolsTracked = _lastSignal.Count,
                ActiveSymbols = _activeTradingModules.Keys.ToList()
            };
        }

        private string GetSignalEmoji(string signal) => signal switch
        {
            "LONG" => "🟢",
            "SHORT" => "🔴",
            _ => "⚪"
        };
    }

    /// <summary>
    /// Статистика автоматической торговли
    /// </summary>
    public class AutoTradingStats
    {
        public bool IsRunning { get; set; }
        public TimeSpan Uptime { get; set; }
        public int ActivePositions { get; set; }
        public int MaxPositions { get; set; }
        public int TotalSymbolsTracked { get; set; }
        public List<string> ActiveSymbols { get; set; } = new();
    }
}
