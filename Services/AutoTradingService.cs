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
        private readonly BinanceDataService _binanceDataService;

        // Торговые модули для активных позиций
        private readonly ConcurrentDictionary<string, TradingModule> _activeTradingModules = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastTradeTime = new();
        private readonly ConcurrentDictionary<string, string> _lastSignal = new();
        private readonly ConcurrentDictionary<string, SimpleStateManager.ActivePosition> _activePositions = new();
        
        // Отслеживание переходов таймфрейма
        private readonly ConcurrentDictionary<string, DateTime> _lastTimeframeMark = new();

        // Timer для обновления пула удален - теперь только в AutonomousEngine
        private volatile bool _isRunning = false;
        private DateTime _startTime;
        private DateTime _systemStartTime;

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
            _binanceDataService = new BinanceDataService(restClient, backendConfig);
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
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⏰ Пауза между сделками: {_autoTradingConfig.MinTimeBetweenTradesMinutes} мин");
                Console.WriteLine();

                _startTime = DateTime.UtcNow;
                _systemStartTime = DateTime.UtcNow; // Запоминаем время запуска системы
                _isRunning = true;

                // Восстановление состояния и синхронизация с биржей
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💾 Восстановление состояния...");
                await RestoreStateAsync();
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔄 Синхронизация с биржей...");
                await SynchronizePositionsAsync();

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

                // HFT движок уже запущен в AutonomousEngine, подключаемся к нему
                // Обновление пула монет происходит в AutonomousEngine, здесь не нужно дублировать

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

            // Убрана проверка силы сигнала - используем настройки стратегии
            
            // Проверка 4: Первые 5 секунд после запуска
            var timeSinceStart = DateTime.UtcNow - _systemStartTime;
            if (timeSinceStart.TotalSeconds < 5)
            {
                return false;
            }
            
            // Убрана проверка перехода таймфрейма - разрешаем торговлю в любое время

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
                var tradingConfig = await CreateTradingConfigAsync(symbol, signal, strategyResult);
                
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
        private async Task<TradingConfig> CreateTradingConfigAsync(string symbol, string signal, StrategyResult strategyResult)
        {
            var side = signal == "LONG" ? "BUY" : "SELL";
            
            // Получаем реальный TickSize для символа
            var tickSize = await _binanceDataService.GetTickSizeAsync(symbol);
            
            // Простой fallback на основе цены
            if (tickSize == null)
            {
                var currentPrice = GetCurrentPrice(symbol);
                if (currentPrice > 1)
                    tickSize = 0.01m;    // Для дорогих монет
                else if (currentPrice > 0.1m)
                    tickSize = 0.001m;   // Для средних монет  
                else if (currentPrice > 0.01m)
                    tickSize = 0.0001m;  // Для дешевых монет
                else
                    tickSize = 0.00001m; // Для очень дешевых монет
                    
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔧 Использован fallback TickSize для {symbol}: {tickSize} (цена: {currentPrice})");
            }
            
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
                TickSize = tickSize.Value,
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
            
            // Используем фиксированную сумму из конфига
            return baseAmount;
        }

        /// <summary>
        /// Проверка перехода таймфрейма
        /// </summary>
        private bool IsTimeframeCrossing(string symbol)
        {
            var now = DateTime.UtcNow;
            DateTime currentMark;
            
            // Определяем текущую отметку таймфрейма
            if (_backendConfig.EnableFifteenSecondTrading)
            {
                // 15-секундный таймфрейм: 00, 15, 30, 45 секунд
                var seconds = (now.Second / 15) * 15;
                currentMark = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, seconds, DateTimeKind.Utc);
            }
            else
            {
                // 1-минутный таймфрейм: начало каждой минуты
                currentMark = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            }
            
            // Проверяем, произошёл ли переход
            if (_lastTimeframeMark.TryGetValue(symbol, out var lastMark))
            {
                if (currentMark <= lastMark)
                {
                    return false; // Не было перехода
                }
                
                // Сохраняем новую отметку и разрешаем торговлю
                _lastTimeframeMark[symbol] = currentMark;
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🕐 ПЕРЕХОД ТАЙМФРЕЙМА: {symbol} → {currentMark:HH:mm:ss}");
                return true;
            }
            else
            {
                // ПЕРВЫЙ РАЗ - инициализируем, но НЕ разрешаем торговлю
                _lastTimeframeMark[symbol] = currentMark;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 ИНИЦИАЛИЗАЦИЯ ТАЙМФРЕЙМА: {symbol} → {currentMark:HH:mm:ss}");
                return false; // НЕ разрешаем торговлю при инициализации
            }
        }

        /// <summary>
        /// Проверка перехода таймфрейма без изменения состояния
        /// </summary>
        private bool IsTimeframeCrossingCheck(string symbol)
        {
            var now = DateTime.UtcNow;
            DateTime currentMark;
            
            // Определяем текущую отметку таймфрейма
            if (_backendConfig.EnableFifteenSecondTrading)
            {
                var seconds = (now.Second / 15) * 15;
                currentMark = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, seconds, DateTimeKind.Utc);
            }
            else
            {
                currentMark = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            }
            
            // Проверяем без изменения состояния
            if (_lastTimeframeMark.TryGetValue(symbol, out var lastMark))
            {
                return currentMark > lastMark;
            }
            
            return true; // Первый раз - разрешаем
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
            
            var timeSinceStart = DateTime.UtcNow - _systemStartTime;
            if (timeSinceStart.TotalSeconds < 5)
                return $"Ожидание {5 - (int)timeSinceStart.TotalSeconds}с после запуска";
            
            return "Неизвестная причина или слабый сигнал";
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
        /// Синхронизация позиций с биржей при запуске
        /// </summary>
        private async Task SynchronizePositionsAsync()
        {
            try
            {
                // Получаем реальные позиции с биржи
                var realPositions = await _binanceDataService.GetRealPositionsAsync();
                
                // Получаем локальные позиции
                var localPositions = _activePositions.ToList();
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Найдено реальных позиций: {realPositions.Count}, локальных: {localPositions.Count}");
                
                // Проверяем локальные позиции на соответствие реальным
                var positionsToRemove = new List<string>();
                foreach (var localPosition in localPositions)
                {
                    if (!realPositions.ContainsKey(localPosition.Key))
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🧹 ОРФАННАЯ ПОЗИЦИЯ: {localPosition.Key} - удаляем из локального состояния");
                        positionsToRemove.Add(localPosition.Key);
                    }
                    else
                    {
                        var realPos = realPositions[localPosition.Key];
                        var localPos = localPosition.Value;
                        
                        // Проверяем соответствие направления
                        if (realPos.Side != localPos.Side)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ НЕСООТВЕТСТВИЕ СТОРОНЫ: {localPosition.Key} Локально:{localPos.Side} vs Реально:{realPos.Side}");
                        }
                        else
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ ПОЗИЦИЯ СИНХРОНИЗИРОВАНА: {localPosition.Key} {localPos.Side}");
                        }
                    }
                }
                
                // Удаляем орфанные позиции
                foreach (var symbol in positionsToRemove)
                {
                    await _stateManager.RemoveActivePositionAsync(symbol);
                    _activePositions.TryRemove(symbol, out _);
                    
                    // Также останавливаем торговый модуль если он активен
                    if (_activeTradingModules.TryRemove(symbol, out var module))
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 Остановлен торговый модуль для орфанной позиции: {symbol}");
                    }
                }
                
                // Показываем реальные позиции которых нет в локальном состоянии
                foreach (var realPosition in realPositions)
                {
                    if (!_activePositions.ContainsKey(realPosition.Key))
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔍 ВНЕШНЯЯ ПОЗИЦИЯ: {realPosition.Key} {realPosition.Value.Side} " +
                                        $"(PnL: {realPosition.Value.PnL:F2} USDT) - открыта вне бота");
                    }
                }
                
                var finalCount = _activePositions.Count;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Синхронизация завершена. Активных позиций бота: {finalCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка синхронизации позиций: {ex.Message}");
                await _stateManager.LogSystemEventAsync("POSITION_SYNC_ERROR", ex.Message, ex.StackTrace);
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

            // Timer обновления пула теперь управляется в AutonomousEngine

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
