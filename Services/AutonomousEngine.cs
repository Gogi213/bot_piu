using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Services;
using Config;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;

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

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 ЗАПУСК АВТОНОМНОГО ДВИЖКА");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ================================");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔄 Автовосстановление: ВКЛЮЧЕНО");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛡️ Максимум попыток: {MaxRestartAttempts} в {RestartCooldownHours} час(а)");
            Console.WriteLine();

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
            
            // 15-секундный сервис (опционально)
            FifteenSecondCandleService? fifteenSecondService = null;
            if (backendConfig.EnableFifteenSecondTrading)
            {
                fifteenSecondService = new FifteenSecondCandleService(socketClient, dataStorage, backendConfig);
            }
            
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

                // Первый сбор данных о монетах
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Сбор данных о монетах...");
                await universeUpdateService.UpdateUniverseAsync();

                // Запускаем 15s сервис если включен
                if (fifteenSecondService != null)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔥 Запуск 15-секундных свечей...");
                    // Получаем только отфильтрованные монеты по объёму и NATR
                    var filteredCoins = dataStorage.GetFilteredCoins(backendConfig.MinVolumeUsdt, backendConfig.MinNatrPercent);
                    var symbols = filteredCoins.Select(c => c.Symbol).ToList();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Отобрано {symbols.Count} монет для 15s прогрева");
                    await fifteenSecondService.StartAsync(symbols);
                }

                // Запускаем HFT движок
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚡ Запуск HFT движка сигналов...");
                await hftSignalEngine.StartAsync();

                // Запускаем автоматическую торговлю
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 Запуск автоматической торговли...");
                await autoTradingService.StartAsync();

                Console.WriteLine();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 АВТОМАТИЧЕСКАЯ ТОРГОВЛЯ ЗАПУЩЕНА!");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] =====================================");
                if (backendConfig.EnableFifteenSecondTrading)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔥 Режим: 15-СЕКУНДНАЯ ТОРГОВЛЯ");
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⏱️ Прогрев: {backendConfig.FifteenSecondWarmupCandles} свечей");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🕐 Режим: 1-МИНУТНАЯ ТОРГОВЛЯ");
                }
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Система будет:");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] • Мониторить рынок 24/7");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] • Генерировать торговые сигналы");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] • Автоматически открывать/закрывать позиции");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] • Управлять рисками и лимитами");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] • Автоматически восстанавливаться после ошибок");
                Console.WriteLine();

                // Сброс счетчика попыток при успешном запуске
                _restartAttempts = 0;

                // Периодическое обновление пула монет
                var updateTimer = new Timer(async _ =>
                {
                    try
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔄 Обновление пула монет...");
                        await universeUpdateService.UpdateUniverseAsync();
                        
                        // Получаем новый список монет
                        var filteredCoins = dataStorage.GetFilteredCoins(backendConfig.MinVolumeUsdt, backendConfig.MinNatrPercent);
                        var newSymbols = filteredCoins.Take(20).Select(c => c.Symbol).ToList(); // Ограничиваем до 20

                        // Умное обновление 15s сервиса - сохраняем прогретые данные
                        if (fifteenSecondService != null)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔥 Умное обновление 15s: {newSymbols.Count} монет");
                            await fifteenSecondService.UpdateSymbolsAsync(newSymbols);
                        }

                        // TODO: Обновление WebSocket подписок для новых монет
                        // (пока оставляем как есть, можно реализовать позже)
                        
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
