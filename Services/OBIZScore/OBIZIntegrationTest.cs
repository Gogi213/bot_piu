using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Services.OBIZScore.Core;
using Services.OBIZScore.Config;
using Models;
using Config;

namespace Services.OBIZScore
{
    /// <summary>
    /// Тестовый класс для проверки интеграции OBIZ-Score стратегии
    /// </summary>
    public class OBIZIntegrationTest
    {
        private readonly OBIZStrategyConfig _obizConfig;
        private readonly StrategyConfig _strategyConfig;
        private readonly AutoTradingConfig _autoConfig;
        private readonly TradingConfig _tradingConfig;

        public OBIZIntegrationTest()
        {
            // Инициализация тестовых конфигураций
            _obizConfig = new OBIZStrategyConfig
            {
                ZScoreThreshold = 1.5m, // Пониженный порог для тестов
                StrongZScoreThreshold = 2.0m,
                VWAPDeviationThreshold = 1.0m,
                ZScoreWindow = 50, // Уменьшенные окна для быстрого тестирования
                ActivityWindow = 100,
                EfficiencyWindow = 25,
                BaseTakeProfit = 0.002m,
                BaseStopLoss = 0.001m,
                MaxHoldTimeSeconds = 120, // 2 минуты для тестов
                EnableDetailedLogging = true,
                SaveSignalHistory = true
            };

            _strategyConfig = new StrategyConfig
            {
                EnableLegacyStrategies = true,
                EnableOBIZStrategy = true,
                Mode = StrategyMode.Combined
            };

            _autoConfig = new AutoTradingConfig
            {
                MaxConcurrentPositions = 3,
                MinTimeBetweenTradesMinutes = 0,
                EnableAutoTrading = true
            };

            _tradingConfig = new TradingConfig
            {
                UsdAmount = 100m,
                TakeProfitPercent = 2.0m,
                StopLossPercent = 1.0m
            };
        }

        /// <summary>
        /// Основной тестовый сценарий
        /// </summary>
        public async Task<TestResults> RunFullIntegrationTestAsync()
        {
            var results = new TestResults();
            
            try
            {
                Console.WriteLine("🚀 Запуск полного теста интеграции OBIZ-Score стратегии...\n");

                // 1. Тест базовых компонентов
                results.BasicComponentsTest = await TestBasicComponentsAsync();
                
                // 2. Тест тиковых данных
                results.TickDataTest = await TestTickDataGenerationAsync();
                
                // 3. Тест OBIZ стратегии
                results.OBIZStrategyTest = await TestOBIZStrategyAsync();
                
                // 4. Тест интегрированного сервиса
                results.IntegratedServiceTest = await TestIntegratedServiceAsync();
                
                // 5. Тест управления позициями
                results.PositionManagementTest = await TestPositionManagementAsync();

                results.OverallSuccess = results.BasicComponentsTest && 
                                       results.TickDataTest && 
                                       results.OBIZStrategyTest && 
                                       results.IntegratedServiceTest && 
                                       results.PositionManagementTest;

                Console.WriteLine($"\n📊 Общий результат теста: {(results.OverallSuccess ? "✅ УСПЕХ" : "❌ ОШИБКА")}");
                
                return results;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Критическая ошибка в тесте: {ex.Message}");
                results.ErrorMessage = ex.Message;
                return results;
            }
        }

        /// <summary>
        /// Тест базовых компонентов
        /// </summary>
        private async Task<bool> TestBasicComponentsAsync()
        {
            Console.WriteLine("1️⃣ Тестирование базовых компонентов...");
            
            try
            {
                // Тест CircularBuffer
                var buffer = new CircularBuffer<decimal>(5);
                for (int i = 1; i <= 10; i++)
                {
                    buffer.Add(i);
                }
                
                if (buffer.Count != 5 || buffer.Last() != 10)
                {
                    Console.WriteLine("❌ CircularBuffer не работает корректно");
                    return false;
                }

                // Тест RollingStatistics
                var stats = new RollingStatistics(10);
                for (int i = 1; i <= 20; i++)
                {
                    stats.Add(i);
                }
                
                if (stats.Count != 10 || Math.Abs(stats.Mean - 15.5m) > 0.1m)
                {
                    Console.WriteLine("❌ RollingStatistics не работает корректно");
                    return false;
                }

                // Тест PositionManager
                var position = new PositionManager();
                var testSignal = new OBIZSignal
                {
                    Direction = TradeDirection.Buy,
                    EntryPrice = 100m,
                    TPPrice = 102m,
                    SLPrice = 99m,
                    Confidence = SignalConfidence.High
                };
                
                position.Open(testSignal, 1m, "TESTUSDT");
                
                if (!position.IsOpen || position.EntryPrice != 100m)
                {
                    Console.WriteLine("❌ PositionManager не работает корректно");
                    return false;
                }

                Console.WriteLine("✅ Базовые компоненты работают корректно");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка в тесте базовых компонентов: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Тест генерации тиковых данных
        /// </summary>
        private async Task<bool> TestTickDataGenerationAsync()
        {
            Console.WriteLine("2️⃣ Тестирование генерации тиковых данных...");
            
            try
            {
                var adapter = new TickDataAdapter();
                
                // Создаем тестовую свечу
                var testCandle = new CandleData
                {
                    OpenTime = DateTime.UtcNow,
                    Open = 100m,
                    High = 102m,
                    Low = 98m,
                    Close = 101m,
                    Volume = 1000
                };

                // Генерируем тики
                var ticks = adapter.ConvertCandleToTicks(testCandle, "TESTUSDT", 10);
                
                if (ticks.Count != 10)
                {
                    Console.WriteLine($"❌ Неверное количество тиков: {ticks.Count} вместо 10");
                    return false;
                }

                // Проверяем качество данных
                bool hasValidOrderBook = ticks.All(t => t.Bids?.Length > 0 && t.Asks?.Length > 0);
                bool hasValidPrices = ticks.All(t => t.Price > 0 && t.BestBid > 0 && t.BestAsk > 0);
                bool hasValidVolumes = ticks.All(t => t.Volume > 0);

                if (!hasValidOrderBook || !hasValidPrices || !hasValidVolumes)
                {
                    Console.WriteLine("❌ Некачественные тиковые данные");
                    return false;
                }

                Console.WriteLine($"✅ Сгенерировано {ticks.Count} качественных тиков");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка в тесте тиковых данных: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Тест OBIZ стратегии
        /// </summary>
        private async Task<bool> TestOBIZStrategyAsync()
        {
            Console.WriteLine("3️⃣ Тестирование OBIZ-Score стратегии...");
            
            try
            {
                var strategy = new OBIZScoreStrategy(_obizConfig);
                var adapter = new TickDataAdapter();
                
                // Генерируем тестовые данные для прогрева
                var testCandles = GenerateTestCandles(100); // 100 свечей для истории
                var allTicks = new List<TickData>();

                foreach (var candle in testCandles)
                {
                    var ticks = adapter.ConvertCandleToTicks(candle, "TESTUSDT", 5);
                    allTicks.AddRange(ticks);
                }

                Console.WriteLine($"Сгенерировано {allTicks.Count} тиков для тестирования");

                // Прогреваем стратегию
                var signalCount = 0;
                var lastDecision = TradingDecision.NoAction;

                foreach (var tick in allTicks)
                {
                    var decision = await strategy.ProcessTickAsync(tick, "TESTUSDT");
                    
                    if (decision.Signal.HasValue)
                    {
                        signalCount++;
                        lastDecision = decision;
                        
                        var signal = decision.Signal.Value;
                        Console.WriteLine($"📈 Сигнал #{signalCount}: {signal.Direction} | " +
                                        $"Confidence: {signal.Confidence} | " +
                                        $"OBIZ Score: {signal.OBIZScore:F2} | " +
                                        $"Regime: {signal.Regime}");
                    }
                }

                var stats = strategy.GetCurrentStats();
                
                Console.WriteLine($"📊 Статистика стратегии:");
                Console.WriteLine($"   Обработано тиков: {stats.TicksProcessed}");
                Console.WriteLine($"   Готовность: {stats.HasSufficientData}");
                Console.WriteLine($"   Сигналов сгенерировано: {signalCount}");
                Console.WriteLine($"   Текущий OBIZ Score: {stats.CurrentOBIZScore:F2}");
                Console.WriteLine($"   Режим рынка: {stats.CurrentRegime}");

                if (!stats.HasSufficientData)
                {
                    Console.WriteLine("❌ Стратегия не получила достаточно данных");
                    return false;
                }

                Console.WriteLine("✅ OBIZ стратегия работает корректно");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка в тесте OBIZ стратегии: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Тест интегрированного сервиса
        /// </summary>
        private async Task<bool> TestIntegratedServiceAsync()
        {
            Console.WriteLine("4️⃣ Тестирование интегрированного сервиса...");
            
            try
            {
                // Создаем заглушку для legacy стратегии
                var legacyStrategy = new MockTradingStrategyService();
                
                var integratedService = new IntegratedStrategyService(
                    legacyStrategy, 
                    _obizConfig, 
                    _strategyConfig);

                // Создаем тестовые данные монет
                var testCoins = GenerateTestCoins(5);

                // Тестируем анализ
                var results = await integratedService.AnalyzeAllCoinsAsync(testCoins);
                
                if (results.Count != testCoins.Count)
                {
                    Console.WriteLine($"❌ Неверное количество результатов: {results.Count} вместо {testCoins.Count}");
                    return false;
                }

                // Проверяем активные сигналы
                var activeSignals = await integratedService.GetActiveSignalsAsync(testCoins);
                
                Console.WriteLine($"📊 Результаты анализа:");
                Console.WriteLine($"   Монет проанализировано: {results.Count}");
                Console.WriteLine($"   Активных сигналов: {activeSignals.Count}");
                
                foreach (var result in results)
                {
                    Console.WriteLine($"   {result.Symbol}: {result.FinalSignal} | " +
                                    $"OBIZ: {result.OBIZScore:F2} | " +
                                    $"Z-Score: {result.ZScore:F2}");
                }

                Console.WriteLine("✅ Интегрированный сервис работает корректно");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка в тесте интегрированного сервиса: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Тест управления позициями
        /// </summary>
        private async Task<bool> TestPositionManagementAsync()
        {
            Console.WriteLine("5️⃣ Тестирование управления позициями...");
            
            try
            {
                var positionManager = new OBIZPositionManager(_obizConfig, _autoConfig, _tradingConfig);
                
                // Создаем тестовый сигнал
                var testSignal = new OBIZSignal
                {
                    Direction = TradeDirection.Buy,
                    EntryPrice = 100m,
                    TPPrice = 102m,
                    SLPrice = 99m,
                    Confidence = SignalConfidence.High,
                    OBIZScore = 2.5m
                };

                // Тестируем открытие позиции
                var openResult = await positionManager.OpenPositionAsync("TESTUSDT", testSignal);
                
                if (!openResult.Success)
                {
                    Console.WriteLine($"❌ Не удалось открыть позицию: {openResult.ErrorMessage}");
                    return false;
                }

                Console.WriteLine($"✅ Позиция открыта: {openResult.Symbol} | " +
                                $"Размер: {openResult.PositionSize:F4} | " +
                                $"Вход: {openResult.EntryPrice:F4}");

                // Получаем информацию о позициях
                var positions = positionManager.GetAllPositions(new Dictionary<string, decimal> 
                { 
                    ["TESTUSDT"] = 101m 
                });

                if (positions.Count != 1)
                {
                    Console.WriteLine($"❌ Неверное количество позиций: {positions.Count}");
                    return false;
                }

                var position = positions[0];
                Console.WriteLine($"📊 Информация о позиции:");
                Console.WriteLine($"   PnL: {position.PnLPercent:F2}%");
                Console.WriteLine($"   Время удержания: {position.HoldingTimeMinutes:F1} мин");

                // Тестируем обновление позиций
                var updateResults = await positionManager.UpdateAllPositionsAsync(new Dictionary<string, decimal> 
                { 
                    ["TESTUSDT"] = 102.5m // Цена выше TP
                });

                if (updateResults.Count != 1 || updateResults[0].Action != "TAKE_PROFIT")
                {
                    Console.WriteLine($"❌ Take Profit не сработал корректно");
                    return false;
                }

                Console.WriteLine("✅ Управление позициями работает корректно");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка в тесте управления позициями: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Генерация тестовых свечей
        /// </summary>
        private List<CandleData> GenerateTestCandles(int count)
        {
            var candles = new List<CandleData>();
            var random = new Random();
            decimal basePrice = 100m;
            var timestamp = DateTime.UtcNow.AddMinutes(-count * 15); // 15-минутные свечи

            for (int i = 0; i < count; i++)
            {
                decimal volatility = 0.02m; // 2% волатильность
                decimal change = (decimal)(random.NextDouble() - 0.5) * volatility * basePrice;
                
                decimal open = basePrice;
                decimal close = basePrice + change;
                decimal high = Math.Max(open, close) * (1 + (decimal)random.NextDouble() * 0.01m);
                decimal low = Math.Min(open, close) * (1 - (decimal)random.NextDouble() * 0.01m);

                candles.Add(new CandleData
                {
                    OpenTime = timestamp.AddMinutes(i * 15),
                    Open = open,
                    High = high,
                    Low = low,
                    Close = close,
                    Volume = 1000 + random.Next(500)
                });

                basePrice = close; // Следующая свеча начинается с цены закрытия
            }

            return candles;
        }

        /// <summary>
        /// Генерация тестовых монет
        /// </summary>
        private List<CoinData> GenerateTestCoins(int count)
        {
            var coins = new List<CoinData>();
            var symbols = new[] { "BTCUSDT", "ETHUSDT", "ADAUSDT", "DOTUSDT", "LINKUSDT" };

            for (int i = 0; i < Math.Min(count, symbols.Length); i++)
            {
                var candles = GenerateTestCandles(50);
                
                coins.Add(new CoinData
                {
                    Symbol = symbols[i],
                    CurrentPrice = candles.Last().Close,
                    Volume24h = 1000000,
                    Natr = 0.6m,
                    RecentCandles = candles
                });
            }

            return coins;
        }
    }

    /// <summary>
    /// Заглушка для legacy стратегии в тестах
    /// </summary>
    public class MockTradingStrategyService : TradingStrategyService
    {
        public MockTradingStrategyService() : base(new BackendConfig(), null)
        {
        }

        public new StrategyResult AnalyzeCoin(CoinData coinData)
        {
            var random = new Random();
            var signals = new[] { "LONG", "SHORT", "FLAT" };
            var signal = signals[random.Next(signals.Length)];

            return new StrategyResult
            {
                Symbol = coinData.Symbol,
                CurrentPrice = coinData.CurrentPrice,
                ZScore = (decimal)(random.NextDouble() * 4 - 2), // -2 до 2
                ZScoreSignal = signal,
                Sma = coinData.CurrentPrice * 0.99m,
                SmaSignal = signal,
                FinalSignal = signal,
                Reason = "Mock test signal",
                Timestamp = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Результаты тестирования
    /// </summary>
    public class TestResults
    {
        public bool BasicComponentsTest { get; set; }
        public bool TickDataTest { get; set; }
        public bool OBIZStrategyTest { get; set; }
        public bool IntegratedServiceTest { get; set; }
        public bool PositionManagementTest { get; set; }
        public bool OverallSuccess { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
