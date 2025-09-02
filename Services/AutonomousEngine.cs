using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Services;
using Services.OBIZScore;
using Services.OBIZScore.Config;
using Services.OBIZScore.Core;
using Config;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Trading;

namespace Services
{
    /// <summary>
    /// Автономный движок с автоматическим восстановлением после ошибок
    /// </summary>
    public class AutonomousEngine
    {
        private readonly SimpleStateManager _stateManager;
        private volatile bool _shouldRun = true;
        private volatile bool _isRunning = false;
        private int _restartAttempts = 0;
        private DateTime _lastRestart = DateTime.MinValue;
        
        // Настройки восстановления
        private const int MaxRestartAttempts = 5;
        private const int RestartCooldownHours = 1;
        private readonly int[] _restartDelays = { 5, 10, 30, 60, 300 }; // секунды
        
        // Конфигурация
        private readonly string _apiKey;
        private readonly string _apiSecret;
        
        public AutonomousEngine(string apiKey, string apiSecret)
        {
            _apiKey = apiKey;
            _apiSecret = apiSecret;
            _stateManager = new SimpleStateManager();
        }

        /// <summary>
        /// Запуск автономного движка
        /// </summary>
        public async Task StartAsync()
        {
            if (_isRunning)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Автономный движок уже запущен");
                return;
            }

            JsonLogger.SystemEvent("AUTONOMOUS_ENGINE_START", "Autonomous engine starting", new Dictionary<string, object>
            {
                ["autoRecovery"] = true,
                ["maxRestartAttempts"] = MaxRestartAttempts,
                ["restartCooldownHours"] = RestartCooldownHours,
                ["restartDelays"] = _restartDelays
            });

            _isRunning = true;
            
            // Основной цикл с автовосстановлением
            while (_shouldRun)
            {
                try
                {
                    // Проверяем лимиты перезапуска
                    if (ShouldStopDueToRestartLimits())
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💀 КРИТИЧЕСКАЯ ОШИБКА: превышен лимит перезапусков");
                        await _stateManager.LogSystemEventAsync("AUTONOMOUS_ENGINE_STOPPED", 
                            $"Exceeded restart limits: {_restartAttempts} attempts in {RestartCooldownHours} hour(s)");
                        break;
                    }

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 Запуск торговой системы (попытка {_restartAttempts + 1})...");
                    
                    // Запускаем основную торговую систему
                    await RunTradingSystemAsync();
                    
                    // Если дошли сюда - система завершилась корректно
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Торговая система завершена корректно");
                    break;
                }
                catch (Exception ex)
                {
                    _restartAttempts++;
                    var isRecoverable = IsRecoverableError(ex);
                    
                    await _stateManager.LogSystemEventAsync("SYSTEM_CRASH", 
                        $"Attempt {_restartAttempts}: {ex.Message}", ex.StackTrace);

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💥 СИСТЕМНАЯ ОШИБКА (попытка {_restartAttempts}):");
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ {ex.Message}");
                    
                    if (!isRecoverable)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💀 КРИТИЧЕСКАЯ ОШИБКА: восстановление невозможно");
                        break;
                    }

                    if (_restartAttempts >= MaxRestartAttempts)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💀 КРИТИЧЕСКАЯ ОШИБКА: достигнут максимум попыток ({MaxRestartAttempts})");
                        break;
                    }

                    // Задержка перед перезапуском
                    var delayIndex = Math.Min(_restartAttempts - 1, _restartDelays.Length - 1);
                    var delay = _restartDelays[delayIndex];
                    
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔄 Перезапуск через {delay} секунд...");
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Осталось попыток: {MaxRestartAttempts - _restartAttempts}");
                    
                    _lastRestart = DateTime.UtcNow;
                    
                    // Ждем с возможностью прерывания
                    for (int i = delay; i > 0 && _shouldRun; i--)
                    {
                        Console.Write($"\r[{DateTime.Now:HH:mm:ss.fff}] ⏳ Перезапуск через {i} сек...");
                        await Task.Delay(1000);
                    }
                    Console.WriteLine();
                }
            }

            _isRunning = false;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 Автономный движок остановлен");
        }

        /// <summary>
        /// Остановка автономного движка
        /// </summary>
        public void Stop()
        {
            _shouldRun = false;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 Запрошена остановка автономного движка...");
        }

        /// <summary>
        /// Запуск основной торговой системы
        /// </summary>
        private async Task RunTradingSystemAsync()
        {
            // Загружаем конфигурацию
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("config.json", optional: false, reloadOnChange: true)
                .Build();

            var tradingConfig = TradingConfig.LoadFromConfiguration(configuration);
            var backendConfig = BackendConfig.LoadFromConfiguration(configuration);
            var autoTradingConfig = AutoTradingConfig.LoadFromConfiguration(configuration);
            var coinSelectionConfig = CoinSelectionConfig.LoadFromConfiguration(configuration);
            var strategyConfig = StrategyConfig.LoadFromConfiguration(configuration);

            // Создаем клиенты Binance
            var restClient = new BinanceRestClient(options =>
            {
                options.ApiCredentials = new ApiCredentials(_apiKey, _apiSecret);
            });

            var socketClient = new BinanceSocketClient(options =>
            {
                options.ApiCredentials = new ApiCredentials(_apiKey, _apiSecret);
            });

            // Создаем сервисы
            var dataStorage = new DataStorageService();
            var binanceDataService = new BinanceDataService(restClient, backendConfig);
            
            // Сервис выбора монет
            var coinSelectionService = new CoinSelectionService(
                coinSelectionConfig,
                backendConfig,
                dataStorage,
                binanceDataService);
            
            // 15-секундный сервис (обязательно для торговли)
            if (!backendConfig.EnableFifteenSecondTrading)
            {
                throw new Exception("15-секундная торговля обязательна - установите EnableFifteenSecondTrading = true в config.json");
            }
            
            var fifteenSecondService = new FifteenSecondCandleService(socketClient, dataStorage, backendConfig);
            
            var tradingStrategyService = new TradingStrategyService(backendConfig, fifteenSecondService);

            var universeUpdateService = new UniverseUpdateService(
                binanceDataService,
                dataStorage,
                backendConfig
            );

            var hftSignalEngine = new HftSignalEngineService(
                tradingStrategyService,
                dataStorage,
                backendConfig
            );

            var webSocketService = new MultiSymbolWebSocketService(socketClient, dataStorage, backendConfig);

            var autoTradingService = new AutoTradingService(
                hftSignalEngine,
                dataStorage,
                universeUpdateService,
                webSocketService,
                tradingStrategyService,
                backendConfig,
                tradingConfig,
                autoTradingConfig,
                restClient,
                socketClient,
                _stateManager
            );

            // События
            autoTradingService.OnTradeOpened += (symbol, signal) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ ПОЗИЦИЯ ОТКРЫТА: {symbol} {signal}");
            };

            autoTradingService.OnTradeClosed += (symbol, result) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🏁 ПОЗИЦИЯ ЗАКРЫТА: {symbol} ({result})");
            };

            autoTradingService.OnError += (message) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ ОШИБКА: {message}");
            };

            // Запускаем систему
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔄 Инициализация системы...");

                // Проверяем соединение с Binance
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔗 Проверка соединения с Binance...");
                var serverTimeResponse = await restClient.SpotApi.ExchangeData.GetServerTimeAsync();
                if (!serverTimeResponse.Success)
                {
                    throw new Exception($"Ошибка соединения с Binance: {serverTimeResponse.Error}");
                }
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Соединение с Binance установлено");

                // ПРОВЕРЯЕМ РЕЖИМ СТРАТЕГИИ СРАЗУ
                if (strategyConfig.EnableOBIZStrategy && strategyConfig.Mode == StrategyMode.OBIZOnly)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🧠 Запуск OBIZ-Score как автономного модуля...");
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚡ Пропускаем загрузку полного пула монет в OBIZ режиме");
                    await RunOBIZAutonomousAsync(configuration, restClient, socketClient, dataStorage, binanceDataService, coinSelectionService, webSocketService);
                    return; // OBIZ работает автономно, не запускаем Legacy систему
                }

                // Загрузка полного пула монет только для Legacy режима
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Сбор данных о монетах для Legacy режима...");
                await universeUpdateService.UpdateUniverseAsync();

                // Обязательно запускаем 15s сервис - это единственный режим торговли
                if (fifteenSecondService == null)
                {
                    throw new Exception("15-секундная торговля обязательна, но сервис не инициализирован");
                }
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔥 Запуск 15-секундных свечей...");
                
                // Логируем режим выбора монет
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 Режим выбора монет: {coinSelectionService.GetConfigInfo()}");
                
                // Получаем монеты через сервис выбора
                var coinSelectionResult = await coinSelectionService.GetTradingCoinsAsync();
                if (!coinSelectionResult.Success)
                {
                    throw new Exception($"Ошибка выбора монет для торговли: {coinSelectionResult.ErrorMessage}");
                }
                
                var symbols = coinSelectionResult.SelectedCoins.Select(c => c.Symbol).ToList();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Отобрано {symbols.Count} монет: {coinSelectionResult.SelectionCriteria}");
                await fifteenSecondService.StartAsync(symbols);

                // Запускаем HFT движок
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚡ Запуск HFT движка сигналов...");
                await hftSignalEngine.StartAsync();

                // Запускаем автоматическую торговлю
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 Запуск автоматической торговли...");
                await autoTradingService.StartAsync();

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Автоматическая торговля запущена (15s режим)");

                // Сброс счетчика попыток при успешном запуске
                _restartAttempts = 0;

                // Упрощенная интеграция lifecycle с обновлением NATR
                webSocketService.OnNatrUpdate += async (symbol, natr) =>
                {
                    if (natr.HasValue)
                    {
                        var coinsToExclude = dataStorage.UpdateCoinNatrWithLifecycle(symbol, natr.Value, backendConfig.MinNatrPercent);
                        
                        // Если есть монеты для исключения - обрабатываем
                        if (coinsToExclude.Count > 0)
                        {
                            try
                            {
                                await fifteenSecondService.RemoveSymbolsAsync(coinsToExclude);
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚫 Исключено монет: {coinsToExclude.Count}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка исключения монет: {ex.Message}");
                            }
                        }
                    }
                };

                // Периодическое обновление пула монет (только поиск новых)
                var updateTimer = new Timer(async _ =>
                {
                    try
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 Поиск новых монет...");
                        await universeUpdateService.UpdateUniverseAsync();
                        
                        // Получаем активные монеты для 15s торговли
                        var activeSymbols = dataStorage.GetActiveTradingCoins(backendConfig.MinVolumeUsdt, backendConfig.MinNatrPercent);

                        // Обновляем 15s сервис с активными монетами
                        if (fifteenSecondService != null && activeSymbols.Count > 0)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔥 Обновление 15s пула: {activeSymbols.Count} активных монет");
                            await fifteenSecondService.UpdateSymbolsAsync(activeSymbols);
                        }

                        // Простая статистика
                        var allCoins = dataStorage.GetAllCoins();
                        var activeCount = activeSymbols.Count;
                        var totalCount = allCoins.Count;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 📊 Пул: {activeCount}/{totalCount} активных монет");
                        
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Пул обновлен");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Ошибка обновления пула: {ex.Message}");
                        await _stateManager.LogSystemEventAsync("UNIVERSE_UPDATE_ERROR", ex.Message, ex.StackTrace);
                    }
                }, null, TimeSpan.Zero, TimeSpan.FromMinutes(backendConfig.UpdateIntervalMinutes));

                // Ожидаем остановки
                while (_shouldRun)
                {
                    await Task.Delay(1000);
                }

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 ОСТАНОВКА СИСТЕМЫ...");

                // Останавливаем таймер
                await updateTimer.DisposeAsync();

                // Останавливаем сервисы
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 Остановка автоматической торговли...");
                await autoTradingService.StopAsync();

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 Остановка HFT движка...");
                await hftSignalEngine.StopAsync();

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Система остановлена корректно");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💥 Ошибка в торговой системе: {ex.Message}");
                throw; // Пробрасываем исключение для обработки в автономном движке
            }
        }

        /// <summary>
        /// Проверка, можно ли восстановиться после ошибки
        /// </summary>
        private bool IsRecoverableError(Exception ex)
        {
            var message = ex.Message.ToLower();
            
            // Неисправимые ошибки
            if (message.Contains("api key") || 
                message.Contains("permission") || 
                message.Contains("unauthorized") ||
                message.Contains("forbidden") ||
                ex is UnauthorizedAccessException ||
                ex is SecurityException)
            {
                return false;
            }

            // Исправимые ошибки (сеть, временные API проблемы и т.д.)
            return true;
        }

        /// <summary>
        /// Проверка лимитов перезапуска
        /// </summary>
        private bool ShouldStopDueToRestartLimits()
        {
            if (_restartAttempts == 0) return false;
            
            var timeSinceLastRestart = DateTime.UtcNow - _lastRestart;
            if (timeSinceLastRestart.TotalHours >= RestartCooldownHours)
            {
                // Прошло достаточно времени - сбрасываем счетчик
                _restartAttempts = 0;
                return false;
            }

            return _restartAttempts >= MaxRestartAttempts;
        }

        /// <summary>
        /// Получение статистики автономного движка
        /// </summary>
        public AutonomousEngineStats GetStats()
        {
            return new AutonomousEngineStats
            {
                IsRunning = _isRunning,
                RestartAttempts = _restartAttempts,
                LastRestart = _lastRestart == DateTime.MinValue ? null : _lastRestart,
                MaxRestartAttempts = MaxRestartAttempts,
                RestartCooldownHours = RestartCooldownHours
            };
        }

        /// <summary>
        /// Запуск OBIZ-Score как автономного модуля
        /// </summary>
        private async Task RunOBIZAutonomousAsync(
            IConfiguration configuration,
            BinanceRestClient restClient,
            BinanceSocketClient socketClient,
            DataStorageService dataStorage,
            BinanceDataService binanceDataService,
            CoinSelectionService coinSelectionService,
            MultiSymbolWebSocketService webSocketService)
        {
            var obizConfig = OBIZStrategyConfig.LoadFromConfiguration(configuration);
            var tradingConfig = TradingConfig.LoadFromConfiguration(configuration);
            var autoTradingConfig = AutoTradingConfig.LoadFromConfiguration(configuration);
            var coinSelectionConfig = CoinSelectionConfig.LoadFromConfiguration(configuration);
            var backendConfig = BackendConfig.LoadFromConfiguration(configuration);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 OBIZ Autonomous Mode Activated");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Configuration: {obizConfig}");

            // Создаем автономный OBIZ сервис
            var obizService = new OBIZAutonomousService(
                obizConfig,
                backendConfig,
                tradingConfig,
                autoTradingConfig,
                coinSelectionConfig,
                dataStorage,
                binanceDataService,
                coinSelectionService,
                webSocketService);

            // Подписываемся на события
            obizService.OnOBIZSignal += async (symbol, signal) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 OBIZ SIGNAL: {symbol} {signal.Direction} | " +
                                 $"Score: {signal.OBIZScore:F2} | Confidence: {signal.Confidence} | Regime: {signal.Regime}");
                
                // Логируем в JSON файл тоже
                Services.OBIZScore.OBIZJsonLogger.Log("INFO", "AUTONOMOUS_ENGINE", 
                    $"🎯 OBIZ SIGNAL RECEIVED: {symbol} {signal.Direction} | Score: {signal.OBIZScore:F2}");
                
                // 🚀 СОЗДАЕМ РЕАЛЬНУЮ СДЕЛКУ ЧЕРЕЗ TradingModule
                await CreateOBIZTradeAsync(restClient, socketClient, symbol, signal, tradingConfig);
            };

            obizService.OnPositionOpened += (symbol, direction) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ OBIZ POSITION OPENED: {symbol} {direction}");
            };

            obizService.OnPositionClosed += (symbol, result) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🏁 OBIZ POSITION CLOSED: {symbol} ({result})");
            };

            obizService.OnError += (message) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ OBIZ ERROR: {message}");
            };

            // Запускаем OBIZ сервис
            var started = await obizService.StartAsync();
            if (!started)
            {
                throw new Exception("Failed to start OBIZ Autonomous Service");
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ OBIZ Autonomous Service running successfully!");

            // Главный цикл мониторинга
            try
            {
                while (_shouldRun)
                {
                    await Task.Delay(5000); // Проверяем каждые 5 секунд

                    // Показываем статистику каждые 30 секунд
                    if (DateTime.UtcNow.Second % 30 == 0)
                    {
                        var stats = obizService.GetStats();
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 OBIZ Status: " +
                                         $"Strategies: {stats.ActiveStrategies}, " +
                                         $"Positions: {stats.PositionStats.TotalOpenPositions}/{stats.PositionStats.MaxAllowedPositions}, " +
                                         $"Symbols: {stats.ActiveSymbols}");
                    }
                }
            }
            finally
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 Stopping OBIZ Autonomous Service...");
                await obizService.StopAsync();
                obizService.Dispose();
            }
        }

        /// <summary>
        /// Создание реальной сделки для OBIZ сигнала через TradingModule
        /// </summary>
        private async Task CreateOBIZTradeAsync(
            BinanceRestClient restClient,
            BinanceSocketClient socketClient,
            string symbol,
            OBIZSignal signal,
            TradingConfig tradingConfig)
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔄 Creating real trade for OBIZ signal: {symbol} {signal.Direction}");
                Services.OBIZScore.OBIZJsonLogger.Log("INFO", "AUTONOMOUS_ENGINE", 
                    $"🔄 Creating real trade for OBIZ signal: {symbol} {signal.Direction}");

                // Конвертируем OBIZ сигнал в формат для TradingModule
                var side = signal.Direction == TradeDirection.Buy ? "BUY" : "SELL";
                
                // Создаем конфигурацию для TradingModule на основе OBIZ сигнала
                var obizTradingConfig = new TradingConfig
                {
                    Symbol = symbol,
                    Side = side,
                    UsdAmount = tradingConfig.UsdAmount, // Используем размер из основной конфигурации
                    TakeProfitPercent = CalculateOBIZTakeProfit(signal),
                    StopLossPercent = CalculateOBIZStopLoss(signal),
                    EnableBreakEven = tradingConfig.EnableBreakEven,
                    BreakEvenActivationPercent = tradingConfig.BreakEvenActivationPercent,
                    BreakEvenStopLossPercent = tradingConfig.BreakEvenStopLossPercent,
                    TickSize = 0.0001m, // Будет скорректировано TradingModule
                    MonitorIntervalSeconds = tradingConfig.MonitorIntervalSeconds
                };

                // Создаем и запускаем TradingModule
                var tradingModule = new Trading.TradingModule(restClient, socketClient, obizTradingConfig);
                
                // Запускаем торговлю в фоновом режиме
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await tradingModule.ExecuteTradeAsync();
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ OBIZ trade completed: {symbol}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ OBIZ trade error {symbol}: {ex.Message}");
                    }
                });

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 OBIZ trade launched: {symbol} {side} | TP: {obizTradingConfig.TakeProfitPercent:P2} | SL: {obizTradingConfig.StopLossPercent:P2}");
                Services.OBIZScore.OBIZJsonLogger.Log("INFO", "AUTONOMOUS_ENGINE", 
                    $"🚀 OBIZ trade launched: {symbol} {side} | TP: {obizTradingConfig.TakeProfitPercent:P2} | SL: {obizTradingConfig.StopLossPercent:P2}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Failed to create OBIZ trade for {symbol}: {ex.Message}");
                Services.OBIZScore.OBIZJsonLogger.Log("ERROR", "AUTONOMOUS_ENGINE", 
                    $"❌ Failed to create OBIZ trade for {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Расчет Take Profit для OBIZ сигнала
        /// </summary>
        private decimal CalculateOBIZTakeProfit(OBIZSignal signal)
        {
            // Используем расстояние от entry до TP из OBIZ сигнала
            var tpDistance = Math.Abs(signal.TPPrice - signal.EntryPrice) / signal.EntryPrice;
            return tpDistance;
        }

        /// <summary>
        /// Расчет Stop Loss для OBIZ сигнала
        /// </summary>
        private decimal CalculateOBIZStopLoss(OBIZSignal signal)
        {
            // Используем расстояние от entry до SL из OBIZ сигнала
            var slDistance = Math.Abs(signal.EntryPrice - signal.SLPrice) / signal.EntryPrice;
            return slDistance;
        }
    }

    /// <summary>
    /// Статистика автономного движка
    /// </summary>
    public class AutonomousEngineStats
    {
        public bool IsRunning { get; set; }
        public int RestartAttempts { get; set; }
        public DateTime? LastRestart { get; set; }
        public int MaxRestartAttempts { get; set; }
        public int RestartCooldownHours { get; set; }
    }
}
