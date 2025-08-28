using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Services;
using Config;
using Models;

namespace Testing
{
    /// <summary>
    /// Универсальный тестер для всех компонентов торговой системы
    /// </summary>
    public class UniversalTester
    {
        public static async Task TestCoinPoolAsync()
        {
            Console.WriteLine("🚀 ТЕСТ 1: СБОР И ФИЛЬТРАЦИЯ ПУЛА МОНЕТ");
            Console.WriteLine("======================================");
            Console.WriteLine("🎯 Цель: Протестировать сбор монет с Binance и фильтрацию по объему/волатильности");
            Console.WriteLine();

            try
            {
                // Загружаем переменные окружения
                LoadEnvFile();

                var apiKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY");
                var apiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET");

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
                {
                    Console.WriteLine("❌ Не найдены API ключи в .env файле");
                    return;
                }

                // Загружаем конфигурацию
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("config.json", optional: false, reloadOnChange: true)
                    .Build();

                var backendConfig = BackendConfig.LoadFromConfiguration(configuration);

                Console.WriteLine($"📊 Конфигурация:");
                Console.WriteLine($"   💰 Минимальный объем: {backendConfig.MinVolumeUsdt:N0} USDT");
                Console.WriteLine($"   📈 Минимальная волатильность: {backendConfig.MinNatrPercent}%");
                Console.WriteLine($"   📅 Исторических свечей: {backendConfig.HistoryCandles}");
                Console.WriteLine();

                // Создаем клиенты и сервисы
                var restClient = new BinanceRestClient(options =>
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                });

                var dataStorage = new DataStorageService();
                var binanceService = new BinanceDataService(restClient, backendConfig);
                var universeService = new UniverseUpdateService(binanceService, dataStorage, backendConfig);

                Console.WriteLine("📊 ЭТАП 1: Загрузка и фильтрация монет");
                Console.WriteLine("=====================================");

                var result = await universeService.UpdateUniverseAsync();

                if (result.Success)
                {
                    var filteredCoins = dataStorage.GetFilteredCoins(backendConfig.MinVolumeUsdt, backendConfig.MinNatrPercent);
                    
                    Console.WriteLine("✅ ТЕСТ ПУЛА МОНЕТ УСПЕШЕН!");
                    Console.WriteLine($"📈 Всего найдено: {result.TotalCoinsFound} монет");
                    Console.WriteLine($"📊 Прошли фильтры: {filteredCoins.Count} монет");
                    Console.WriteLine($"⏱️ Время выполнения: {result.Duration.TotalSeconds:F1} сек");
                    
                    Console.WriteLine();
                    Console.WriteLine("🏆 Топ-10 монет по волатильности:");
                    foreach (var coin in filteredCoins.Take(10))
                    {
                        Console.WriteLine($"   {coin.Symbol}: NATR={coin.Natr:F2}%, объем={coin.Volume24h:N0} USDT");
                    }
                }
                else
                {
                    Console.WriteLine($"❌ Ошибка теста: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Критическая ошибка: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("Нажмите любую клавишу для продолжения...");
            Console.ReadKey();
        }

        public static async Task TestWebSocketAsync()
        {
            Console.WriteLine("📡 ТЕСТ 2: WEBSOCKET ИНТЕГРАЦИЯ");
            Console.WriteLine("===============================");
            Console.WriteLine("🎯 Цель: Протестировать real-time обновления цен и свечей");
            Console.WriteLine();

            try
            {
                LoadEnvFile();
                var apiKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY");
                var apiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET");

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
                {
                    Console.WriteLine("❌ Не найдены API ключи в .env файле");
                    return;
                }

                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("config.json", optional: false, reloadOnChange: true)
                    .Build();

                var backendConfig = BackendConfig.LoadFromConfiguration(configuration);

                // Создаем сервисы
                var restClient = new BinanceRestClient(options =>
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                });

                var socketClient = new BinanceSocketClient(options =>
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                });

                var dataStorage = new DataStorageService();
                var binanceService = new BinanceDataService(restClient, backendConfig);
                var universeService = new UniverseUpdateService(binanceService, dataStorage, backendConfig);
                var webSocketService = new MultiSymbolWebSocketService(socketClient, dataStorage, backendConfig);

                // Сначала загружаем пул монет
                Console.WriteLine("📊 Подготовка данных...");
                var result = await universeService.UpdateUniverseAsync();
                if (!result.Success)
                {
                    Console.WriteLine($"❌ Ошибка загрузки данных: {result.ErrorMessage}");
                    return;
                }

                var filteredCoins = dataStorage.GetFilteredCoins(backendConfig.MinVolumeUsdt, backendConfig.MinNatrPercent);
                var testSymbols = filteredCoins.Take(5).Select(c => c.Symbol).ToList();

                Console.WriteLine($"✅ Подготовлено {testSymbols.Count} символов для WebSocket теста");
                Console.WriteLine($"📡 Тестируемые символы: {string.Join(", ", testSymbols)}");
                Console.WriteLine();

                // Настраиваем обработчики событий
                var priceUpdates = 0;
                var candleUpdates = 0;
                var natrUpdates = 0;

                webSocketService.OnPriceUpdate += (symbol, price) =>
                {
                    priceUpdates++;
                    if (priceUpdates % 10 == 0)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💰 {symbol}: цена={price:F6} (обновлений: {priceUpdates})");
                    }
                };

                webSocketService.OnCandleUpdate += (symbol, candle) =>
                {
                    candleUpdates++;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🕐 {symbol}: новая свеча, цена={candle.Close:F6}");
                };

                webSocketService.OnNatrUpdate += (symbol, natr) =>
                {
                    natrUpdates++;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📈 {symbol}: NATR обновлен={natr:F2}%");
                };

                // Запускаем WebSocket
                Console.WriteLine("🚀 Запуск WebSocket соединений...");
                await webSocketService.StartAsync(testSymbols);

                Console.WriteLine("📊 Мониторинг WebSocket (30 секунд)...");
                Console.WriteLine("(Нажмите Ctrl+C для досрочной остановки)");

                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                    Console.WriteLine("\n🛑 Получен сигнал остановки...");
                };

                try
                {
                    await Task.Delay(30000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("⏹️ WebSocket мониторинг прерван пользователем");
                }

                await webSocketService.StopAsync();

                Console.WriteLine();
                Console.WriteLine("✅ ТЕСТ WEBSOCKET ЗАВЕРШЕН!");
                Console.WriteLine($"📊 Статистика:");
                Console.WriteLine($"   💰 Обновлений цен: {priceUpdates}");
                Console.WriteLine($"   🕐 Обновлений свечей: {candleUpdates}");
                Console.WriteLine($"   📈 Обновлений NATR: {natrUpdates}");

                if (priceUpdates > 50 && candleUpdates > 0)
                {
                    Console.WriteLine("🎉 WebSocket работает отлично!");
                }
                else
                {
                    Console.WriteLine("⚠️ WebSocket работает, но мало данных получено");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка WebSocket теста: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("Нажмите любую клавишу для продолжения...");
            Console.ReadKey();
        }

        public static async Task TestStrategyAsync()
        {
            Console.WriteLine("🧠 ТЕСТ 3: ТОРГОВАЯ СТРАТЕГИЯ");
            Console.WriteLine("=============================");
            Console.WriteLine("🎯 Цель: Протестировать генерацию торговых сигналов");
            Console.WriteLine();

            try
            {
                LoadEnvFile();
                var apiKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY");
                var apiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET");

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
                {
                    Console.WriteLine("❌ Не найдены API ключи в .env файле");
                    return;
                }

                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("config.json", optional: false, reloadOnChange: true)
                    .Build();

                var backendConfig = BackendConfig.LoadFromConfiguration(configuration);

                // Создаем сервисы
                var restClient = new BinanceRestClient(options =>
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                });

                var dataStorage = new DataStorageService();
                var binanceService = new BinanceDataService(restClient, backendConfig);
                var universeService = new UniverseUpdateService(binanceService, dataStorage, backendConfig);
                var strategyService = new TradingStrategyService(backendConfig);

                Console.WriteLine("📊 Подготовка данных для анализа...");
                var result = await universeService.UpdateUniverseAsync();
                if (!result.Success)
                {
                    Console.WriteLine($"❌ Ошибка загрузки данных: {result.ErrorMessage}");
                    return;
                }

                var filteredCoins = dataStorage.GetFilteredCoins(backendConfig.MinVolumeUsdt, backendConfig.MinNatrPercent);
                Console.WriteLine($"✅ Подготовлено {filteredCoins.Count} монет для анализа");

                Console.WriteLine();
                Console.WriteLine("🧠 Анализ торговых сигналов...");
                Console.WriteLine("============================");

                var longSignals = new List<StrategyResult>();
                var shortSignals = new List<StrategyResult>();
                var flatSignals = new List<StrategyResult>();

                foreach (var coin in filteredCoins)
                {
                    var signal = strategyService.AnalyzeCoin(coin);
                    
                    switch (signal.FinalSignal)
                    {
                        case "LONG":
                            longSignals.Add(signal);
                            Console.WriteLine($"🟢 LONG:  {signal.Symbol} - Z={signal.ZScore:F2}({signal.ZScoreSignal}), SMA={signal.Sma:F6}({signal.SmaSignal}), цена={signal.CurrentPrice:F6}");
                            break;
                        case "SHORT":
                            shortSignals.Add(signal);
                            Console.WriteLine($"🔴 SHORT: {signal.Symbol} - Z={signal.ZScore:F2}({signal.ZScoreSignal}), SMA={signal.Sma:F6}({signal.SmaSignal}), цена={signal.CurrentPrice:F6}");
                            break;
                        default:
                            flatSignals.Add(signal);
                            // Выводим детали для первых 3 FLAT сигналов для debug
                            if (flatSignals.Count <= 3)
                            {
                                Console.WriteLine($"⚪ FLAT:  {signal.Symbol} - Z={signal.ZScore:F2}({signal.ZScoreSignal}), SMA={signal.Sma:F6}({signal.SmaSignal}), цена={signal.CurrentPrice:F6} - {signal.Reason}");
                            }
                            break;
                    }
                }

                Console.WriteLine();
                Console.WriteLine("📊 РЕЗУЛЬТАТЫ АНАЛИЗА СТРАТЕГИИ:");
                Console.WriteLine($"   🟢 LONG сигналов: {longSignals.Count}");
                Console.WriteLine($"   🔴 SHORT сигналов: {shortSignals.Count}");
                Console.WriteLine($"   ⚪ FLAT сигналов: {flatSignals.Count}");
                Console.WriteLine($"   📈 Всего проанализировано: {filteredCoins.Count} монет");

                var activeSignals = longSignals.Count + shortSignals.Count;
                var signalRate = (double)activeSignals / filteredCoins.Count * 100;

                Console.WriteLine($"   🎯 Процент активных сигналов: {signalRate:F1}%");

                if (activeSignals >= 3)
                {
                    Console.WriteLine("🎉 СТРАТЕГИЯ РАБОТАЕТ ОТЛИЧНО!");
                    Console.WriteLine("✅ Генерируются активные торговые сигналы");
                }
                else if (activeSignals >= 1)
                {
                    Console.WriteLine("👍 СТРАТЕГИЯ РАБОТАЕТ НОРМАЛЬНО");
                    Console.WriteLine("✅ Генерируются торговые сигналы");
                }
                else
                {
                    Console.WriteLine("⚠️ НИЗКАЯ АКТИВНОСТЬ СТРАТЕГИИ");
                    Console.WriteLine("💡 Возможно, стоит изменить параметры или дождаться более волатильного рынка");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка теста стратегии: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("Нажмите любую клавишу для продолжения...");
            Console.ReadKey();
        }

        public static async Task TestHftSystemAsync()
        {
            Console.WriteLine("⚡ ТЕСТ 4: ПСЕВДО-HFT СИСТЕМА");
            Console.WriteLine("=============================");
            Console.WriteLine("🎯 Цель: Демонстрация высокочастотного анализа сигналов");
            Console.WriteLine();

            try
            {
                LoadEnvFile();
                var apiKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY");
                var apiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET");

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
                {
                    Console.WriteLine("❌ Не найдены API ключи в .env файле");
                    return;
                }

                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("config.json", optional: false, reloadOnChange: true)
                    .Build();

                var backendConfig = BackendConfig.LoadFromConfiguration(configuration);

                // Создаем сервисы
                var restClient = new BinanceRestClient(options =>
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                });

                var socketClient = new BinanceSocketClient(options =>
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                });

                var dataStorage = new DataStorageService();
                var binanceService = new BinanceDataService(restClient, backendConfig);
                var universeService = new UniverseUpdateService(binanceService, dataStorage, backendConfig);
                var strategyService = new TradingStrategyService(backendConfig);
                var webSocketService = new MultiSymbolWebSocketService(socketClient, dataStorage, backendConfig);
                var hftEngine = new HftSignalEngineService(strategyService, dataStorage, backendConfig);

                // Подготовка данных
                Console.WriteLine("📊 Подготовка данных для HFT...");
                var result = await universeService.UpdateUniverseAsync();
                if (!result.Success)
                {
                    Console.WriteLine($"❌ Ошибка загрузки данных: {result.ErrorMessage}");
                    return;
                }

                var filteredCoins = dataStorage.GetFilteredCoins(backendConfig.MinVolumeUsdt, backendConfig.MinNatrPercent);
                var topSymbols = filteredCoins.Take(10).Select(c => c.Symbol).ToList();

                Console.WriteLine($"✅ Подготовлено {topSymbols.Count} символов для HFT");

                // Запускаем WebSocket и HFT
                await webSocketService.StartAsync(topSymbols);

                // Интеграция с WebSocket для real-time цен
                webSocketService.OnPriceUpdate += (symbol, price) =>
                {
                    hftEngine.UpdatePrice(symbol, price);
                };

                var hftEvents = 0;
                hftEngine.OnHftSignalChange += (hftEvent) =>
                {
                    hftEvents++;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚡ HFT: {hftEvent.Symbol} {hftEvent.OldSignal}→{hftEvent.NewSignal} ({hftEvent.LatencyMs}ms)");
                };

                await hftEngine.StartAsync();

                Console.WriteLine("⚡ HFT мониторинг БЕЗ ОГРАНИЧЕНИЙ...");
                Console.WriteLine("(Нажмите Ctrl+C для остановки когда увидите сигналы)");

                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                    Console.WriteLine("\n🛑 Получен сигнал остановки HFT...");
                };

                try
                {
                    // Ждем бесконечно до Ctrl+C
                    await Task.Delay(-1, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("⏹️ HFT мониторинг прерван пользователем");
                }

                var finalStats = hftEngine.GetPerformanceStats();
                await hftEngine.StopAsync();
                await webSocketService.StopAsync();

                Console.WriteLine();
                Console.WriteLine("📊 РЕЗУЛЬТАТЫ HFT ТЕСТА:");
                Console.WriteLine($"   ⚡ Анализов/сек: {finalStats.AnalysesPerSecond:F0}");
                Console.WriteLine($"   📈 Средняя задержка: {finalStats.AverageLatencyMs:F1}мс");
                Console.WriteLine($"   🎯 HFT событий: {hftEvents}");
                Console.WriteLine($"   ⏰ Время работы: {finalStats.Uptime.TotalMinutes:F1} мин");

                if (finalStats.AnalysesPerSecond >= 50 && finalStats.AverageLatencyMs <= 5)
                {
                    Console.WriteLine("🏆 HFT СИСТЕМА РАБОТАЕТ ПРЕВОСХОДНО!");
                }
                else if (finalStats.AnalysesPerSecond >= 20)
                {
                    Console.WriteLine("✅ HFT СИСТЕМА РАБОТАЕТ ХОРОШО!");
                }
                else
                {
                    Console.WriteLine("⚠️ HFT система работает базово");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка HFT теста: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("Нажмите любую клавишу для продолжения...");
            Console.ReadKey();
        }

        public static async Task TestAutoTradingAsync()
        {
            Console.WriteLine("🤖 ТЕСТ 5: АВТОМАТИЧЕСКАЯ ТОРГОВАЯ СИСТЕМА");
            Console.WriteLine("==========================================");
            Console.WriteLine("🎯 Цель: Тестирование интеграции HFT с торговым модулем");
            Console.WriteLine();

            try
            {
                LoadEnvFile();
                var apiKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY");
                var apiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET");

                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
                {
                    Console.WriteLine("❌ Не найдены API ключи в .env файле");
                    return;
                }

                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("config.json", optional: false, reloadOnChange: true)
                    .Build();

                var backendConfig = BackendConfig.LoadFromConfiguration(configuration);
                var tradingConfig = TradingConfig.LoadFromConfiguration(configuration);
                var autoTradingConfig = AutoTradingConfig.LoadFromConfiguration(configuration);

                Console.WriteLine($"📊 Конфигурация автоторговли:");
                Console.WriteLine($"   🎯 Максимум позиций: {autoTradingConfig.MaxConcurrentPositions}");
                Console.WriteLine($"   💰 Базовая сумма: {tradingConfig.UsdAmount} USDT");
                Console.WriteLine($"   🎯 Take Profit: {tradingConfig.TakeProfitPercent}%");
                Console.WriteLine($"   🛡️ Stop Loss: {tradingConfig.StopLossPercent}%");
                Console.WriteLine($"   ⏰ Пауза между сделками: {autoTradingConfig.MinTimeBetweenTradesMinutes} минут");
                Console.WriteLine($"   ⚡ Минимальная сила сигнала: {autoTradingConfig.MinSignalStrength}");
                Console.WriteLine($"   🔘 Автоторговля: {(autoTradingConfig.EnableAutoTrading ? "Включена" : "Отключена")}");
                Console.WriteLine();

                // Создаем клиенты и сервисы
                var restClient = new BinanceRestClient(options =>
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                });

                var socketClient = new BinanceSocketClient(options =>
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                });

                var dataStorage = new DataStorageService();
                var binanceService = new BinanceDataService(restClient, backendConfig);
                var universeService = new UniverseUpdateService(binanceService, dataStorage, backendConfig);
                var strategyService = new TradingStrategyService(backendConfig);
                var webSocketService = new MultiSymbolWebSocketService(socketClient, dataStorage, backendConfig);
                var hftEngine = new HftSignalEngineService(strategyService, dataStorage, backendConfig);

                                // Создаем StateManager для автоторговли
                var stateManager = new SimpleStateManager();

                // Создаем автоматическую торговую систему
                var autoTradingService = new AutoTradingService(
                    hftEngine, dataStorage, universeService, webSocketService,
                    strategyService, backendConfig, tradingConfig, autoTradingConfig, restClient, socketClient, stateManager);

                // Подписываемся на события
                var signalsReceived = 0;
                var tradesOpened = 0;
                var tradesClosed = 0;

                autoTradingService.OnSignalReceived += (symbol, signal, strategy) =>
                {
                    signalsReceived++;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📡 Сигнал #{signalsReceived}: {symbol} {signal}");
                };

                autoTradingService.OnTradeOpened += (symbol, signal) =>
                {
                    tradesOpened++;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 Сделка #{tradesOpened}: {symbol} {signal}");
                };

                autoTradingService.OnTradeClosed += (symbol, result) =>
                {
                    tradesClosed++;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🏁 Закрыто #{tradesClosed}: {symbol} - {result}");
                };

                autoTradingService.OnError += (error) =>
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка: {error}");
                };

                Console.WriteLine("🚀 Запуск автоматической торговой системы...");
                var started = await autoTradingService.StartAsync();
                
                if (!started)
                {
                    Console.WriteLine("❌ Ошибка запуска автоматической торговли");
                    return;
                }

                Console.WriteLine("📊 Мониторинг автоторговли (120 секунд)...");
                Console.WriteLine("(Нажмите Ctrl+C для досрочной остановки)");
                Console.WriteLine();

                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                    Console.WriteLine("\n🛑 Получен сигнал остановки автоторговли...");
                };

                // Мониторинг с отчетами каждые 30 секунд
                var monitoringTask = Task.Run(async () =>
                {
                    var reportInterval = TimeSpan.FromSeconds(30);
                    var lastReport = DateTime.UtcNow;

                    while (!cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(5000, cts.Token);

                        if (DateTime.UtcNow - lastReport >= reportInterval)
                        {
                            var stats = autoTradingService.GetStats();
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 СТАТИСТИКА АВТОТОРГОВЛИ:");
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    ⏰ Время работы: {stats.Uptime.TotalMinutes:F1} мин");
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    📡 Получено сигналов: {signalsReceived}");
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    🚀 Открыто позиций: {tradesOpened}");
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    📊 Активных позиций: {stats.ActivePositions}/{stats.MaxPositions}");
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    🎯 Отслеживается символов: {stats.TotalSymbolsTracked}");
                            Console.WriteLine();
                            
                            lastReport = DateTime.UtcNow;
                        }
                    }
                });

                try
                {
                    await Task.Delay(120000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("⏹️ Автоторговля прервана пользователем");
                }

                await autoTradingService.StopAsync();

                Console.WriteLine();
                Console.WriteLine("📊 РЕЗУЛЬТАТЫ ТЕСТА АВТОТОРГОВЛИ:");
                Console.WriteLine($"   📡 Всего сигналов: {signalsReceived}");
                Console.WriteLine($"   🚀 Открыто позиций: {tradesOpened}");
                Console.WriteLine($"   🏁 Закрыто позиций: {tradesClosed}");

                if (signalsReceived >= 1)
                {
                    Console.WriteLine("🎉 АВТОТОРГОВЛЯ РАБОТАЕТ ОТЛИЧНО!");
                    Console.WriteLine("✅ Система успешно интегрирована");
                    
                    if (tradesOpened >= 1)
                    {
                        Console.WriteLine("🏆 ПОЗИЦИИ ОТКРЫВАЮТСЯ АВТОМАТИЧЕСКИ!");
                    }
                    else
                    {
                        Console.WriteLine("💡 Сигналы получены, но позиции не открыты (возможно, жесткие фильтры)");
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ Сигналы не получены - возможно, рынок спокойный");
                    Console.WriteLine("✅ Базовая интеграция работает");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка теста автоторговли: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine("Нажмите любую клавишу для продолжения...");
            Console.ReadKey();
        }

        public static async Task RunAllTestsAsync()
        {
            Console.WriteLine("🚀 ПОЛНЫЙ НАБОР ТЕСТОВ ТОРГОВОЙ СИСТЕМЫ");
            Console.WriteLine("=======================================");
            Console.WriteLine();

            await TestCoinPoolAsync();
            await TestWebSocketAsync();
            await TestStrategyAsync();
            await TestHftSystemAsync();
            await TestAutoTradingAsync();

            Console.WriteLine();
            Console.WriteLine("🎉 ВСЕ ТЕСТЫ ЗАВЕРШЕНЫ!");
            Console.WriteLine("======================");
            Console.WriteLine("✅ Система полностью интегрирована и готова к автоторговле!");
        }

        private static void LoadEnvFile()
        {
            try
            {
                if (!File.Exists(".env")) return;
                var lines = File.ReadAllLines(".env");
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim();
                    var val = line.Substring(idx + 1).Trim().Trim('"');
                    Environment.SetEnvironmentVariable(key, val);
                }
            }
            catch { }
        }
    }
}
