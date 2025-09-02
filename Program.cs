using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using Binance.Net;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Trading;
using Config;
using Testing;
using Microsoft.Extensions.Configuration;
using Services;
using Services.OBIZScore;
using Services.OBIZScore.Config;
using Models;

class Program
{
    static void LoadEnvFile()
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

    static async Task Main(string[] args)
    {
        // Проверяем аргументы командной строки
        if (args.Length > 0)
        {
            switch (args[0].ToLower())
            {
                case "test-pool":
                    await UniversalTester.TestCoinPoolAsync();
                    return;
                
                case "test-websocket":
                    await UniversalTester.TestWebSocketAsync();
                    return;
                
                case "test-strategy":
                    await UniversalTester.TestStrategyAsync();
                    return;
                
                case "test-hft":
                    await UniversalTester.TestHftSystemAsync();
                    return;
                
                case "test-auto":
                    await UniversalTester.TestAutoTradingAsync();
                    return;
                
                case "test-all":
                    await UniversalTester.RunAllTestsAsync();
                    return;
                
                case "test-coins":
                    await TestCoinSelectionAsync();
                    return;
                
                case "test-obiz":
                    await TestOBIZStrategyAsync();
                    return;
                
                case "test-components":
                    await TestOBIZComponentsAsync();
                    return;
                    
                default:
                    Console.WriteLine("🚀 ДОСТУПНЫЕ КОМАНДЫ ТЕСТИРОВАНИЯ:");
                    Console.WriteLine("==================================");
                    Console.WriteLine("  test-pool       - Тест сбора и фильтрации пула монет");
                    Console.WriteLine("  test-websocket  - Тест WebSocket real-time данных");
                    Console.WriteLine("  test-strategy   - Тест торговой стратегии и сигналов");
                    Console.WriteLine("  test-hft        - Тест псевдо-HFT системы");
                    Console.WriteLine("  test-auto       - Тест автоматической торговли");
                    Console.WriteLine("  test-all        - Запуск всех тестов последовательно");
                    Console.WriteLine("  test-coins      - Тест выбора монет для торговли");
                    Console.WriteLine("  test-obiz       - Тест OBIZ-Score стратегии");
                    Console.WriteLine("  test-components - Тест компонентов OBIZ (trades, orderbook, websockets)");
                    Console.WriteLine();
                    Console.WriteLine("💡 Для обычной торговли запустите без параметров");
                    return;
            }
        }

        // АВТОНОМНЫЙ РЕЖИМ ТОРГОВЛИ с автовосстановлением
        LoadEnvFile();
        
        // Загружаем ключи из .env
        var apiKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY");
        var apiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET");

        JsonLogger.SystemEvent("SYSTEM_START", "Bot system started", new Dictionary<string, object>
        {
            ["hasApiKeys"] = !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret),
            ["startTime"] = DateTime.UtcNow
        });

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            Console.WriteLine("❌ Не найдены API ключи в .env файле");
            Console.WriteLine("Для тестирования пула монет используйте: dotnet run test-pool");
            return;
        }

        Console.WriteLine("🚀 ЗАПУСК АВТОНОМНОЙ ТОРГОВОЙ СИСТЕМЫ");
        Console.WriteLine("=====================================");
        Console.WriteLine("🤖 Режим: ПОЛНАЯ АВТОНОМНОСТЬ");
        Console.WriteLine("🔄 Автовосстановление: ВКЛЮЧЕНО");
        Console.WriteLine("💾 Персистентное состояние: ВКЛЮЧЕНО");
        Console.WriteLine("🛡️ Надежные WebSocket: ВКЛЮЧЕНЫ");
        Console.WriteLine();

        // Создаем автономный движок
        var autonomousEngine = new AutonomousEngine(apiKey, apiSecret);

        // Обработка Ctrl+C для корректной остановки
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("🛑 Получен сигнал остановки...");
            autonomousEngine.Stop();
            cts.Cancel();
        };

        try
        {
            // Запускаем автономный движок
            var autonomousTask = autonomousEngine.StartAsync();
            
            // Ожидаем завершения или сигнала остановки
            var delayTask = Task.Delay(-1, cts.Token);
            
            await Task.WhenAny(autonomousTask, delayTask);
            
            // Если автономный движок завершился сам - ждем его корректного завершения
            if (autonomousTask.IsCompleted)
            {
                await autonomousTask;
            }
            else
            {
                // Если получили Ctrl+C - ждем остановки движка
                try
                {
                    await autonomousTask;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка при остановке: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("🛑 Остановка по требованию пользователя");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"💥 Критическая ошибка автономного движка: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }

        // Финальная информация
        var uptime = DateTime.UtcNow - DateTime.UtcNow; // Простая заглушка
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 Время работы: {uptime.TotalHours:F1} часов");
        
        Console.WriteLine("✅ Автономная торговая система завершена");
    }

    /// <summary>
    /// Тестирование системы выбора монет
    /// </summary>
    static async Task TestCoinSelectionAsync()
    {
        LoadEnvFile();
        
        Console.WriteLine("🎯 ТЕСТ СИСТЕМЫ ВЫБОРА МОНЕТ");
        Console.WriteLine("=============================");
        
        try
        {
            // Загружаем конфигурацию
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("config.json", optional: false, reloadOnChange: true)
                .Build();

            var backendConfig = BackendConfig.LoadFromConfiguration(configuration);
            var coinSelectionConfig = CoinSelectionConfig.LoadFromConfiguration(configuration);

            Console.WriteLine($"📊 Конфигурация выбора: {coinSelectionConfig}");
            Console.WriteLine();

            // Инициализируем сервисы (без API ключей для тестирования)
            var restClient = new BinanceRestClient();
            var dataStorage = new DataStorageService();
            var binanceDataService = new BinanceDataService(restClient, backendConfig);
            
            var coinSelectionService = new CoinSelectionService(
                coinSelectionConfig,
                backendConfig,
                dataStorage,
                binanceDataService);

            // Тестируем выбор монет
            Console.WriteLine("🔍 Получение списка монет для торговли...");
            var result = await coinSelectionService.GetTradingCoinsAsync();

            if (result.Success)
            {
                Console.WriteLine($"✅ {result}");
                Console.WriteLine($"📋 Выбранные монеты ({result.SelectedCoins.Count}):");
                
                foreach (var coin in result.SelectedCoins.Take(10)) // Показываем первые 10
                {
                    Console.WriteLine($"   💰 {coin.Symbol}: {coin.CurrentPrice:F4} USDT | Volume: {coin.Volume24h:N0}");
                }
                
                if (result.SelectedCoins.Count > 10)
                {
                    Console.WriteLine($"   ... и еще {result.SelectedCoins.Count - 10} монет");
                }

                if (result.MissingSymbols.Any())
                {
                    Console.WriteLine($"⚠️ Не найдены: {string.Join(", ", result.MissingSymbols)}");
                }
            }
            else
            {
                Console.WriteLine($"❌ Ошибка: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"💥 Критическая ошибка: {ex.Message}");
        }
        
        Console.WriteLine();
        Console.WriteLine("✅ Тест выбора монет завершен");
    }

    /// <summary>
    /// Тестирование OBIZ-Score стратегии
    /// </summary>
    static async Task TestOBIZStrategyAsync()
    {
        Console.WriteLine("🧠 ТЕСТ OBIZ-SCORE СТРАТЕГИИ");
        Console.WriteLine("=============================");
        
        try
        {
            // Загружаем конфигурацию
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("config.json", optional: false, reloadOnChange: true)
                .Build();

            var obizConfig = OBIZStrategyConfig.LoadFromConfiguration(configuration);
            var strategyConfig = StrategyConfig.LoadFromConfiguration(configuration);

            Console.WriteLine($"📊 OBIZ конфигурация: {obizConfig}");
            Console.WriteLine($"🎯 Режим стратегии: {strategyConfig.Mode}");
            Console.WriteLine($"✅ OBIZ включена: {strategyConfig.EnableOBIZStrategy}");
            Console.WriteLine();

            // Запускаем тест интеграции
            var test = new OBIZIntegrationTest();
            var results = await test.RunFullIntegrationTestAsync();

            Console.WriteLine();
            Console.WriteLine("📊 РЕЗУЛЬТАТЫ ТЕСТИРОВАНИЯ:");
            Console.WriteLine($"   Базовые компоненты: {(results.BasicComponentsTest ? "✅" : "❌")}");
            Console.WriteLine($"   Тиковые данные: {(results.TickDataTest ? "✅" : "❌")}");
            Console.WriteLine($"   OBIZ стратегия: {(results.OBIZStrategyTest ? "✅" : "❌")}");
            Console.WriteLine($"   Интегрированный сервис: {(results.IntegratedServiceTest ? "✅" : "❌")}");
            Console.WriteLine($"   Управление позициями: {(results.PositionManagementTest ? "✅" : "❌")}");
            Console.WriteLine();
            Console.WriteLine($"🎯 ОБЩИЙ РЕЗУЛЬТАТ: {(results.OverallSuccess ? "✅ УСПЕХ" : "❌ ОШИБКА")}");

            if (!results.OverallSuccess && !string.IsNullOrEmpty(results.ErrorMessage))
            {
                Console.WriteLine($"❌ Ошибка: {results.ErrorMessage}");
            }

            Console.WriteLine();
            Console.WriteLine("💡 Для активации OBIZ стратегии установите в config.json:");
            Console.WriteLine("   \"Strategy\": { \"Mode\": \"OBIZOnly\", \"EnableOBIZStrategy\": true }");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"💥 Критическая ошибка: {ex.Message}");
        }
        
        Console.WriteLine();
        Console.WriteLine("✅ Тест OBIZ-Score стратегии завершен");
    }

    /// <summary>
    /// Тестирование компонентов OBIZ-Score стратегии
    /// </summary>
    static async Task TestOBIZComponentsAsync()
    {
        Console.WriteLine("🧪 ТЕСТ КОМПОНЕНТОВ OBIZ-SCORE СТРАТЕГИИ");
        Console.WriteLine("========================================");
        
        try
        {
            Console.WriteLine("Выберите тест:");
            Console.WriteLine("1 - Trades (торговые данные)");
            Console.WriteLine("2 - OrderBook (стакан заявок)");
            Console.WriteLine("3 - WebSocket (реальные данные)");
            Console.WriteLine("4 - Integration (интеграция всех компонентов)");
            Console.WriteLine("5 - ALL (все тесты подряд)");
            Console.WriteLine();
            Console.Write("Введите номер теста (1-5): ");
            
            var input = Console.ReadLine();
            
            using var tester = new ComponentTester();
            
            switch (input)
            {
                case "1":
                    Console.WriteLine("🎯 Запуск теста Trades...");
                    await tester.TestTradesAsync("ETHUSDT");
                    break;
                    
                case "2":
                    Console.WriteLine("🎯 Запуск теста OrderBook...");
                    await tester.TestOrderBookAsync("ETHUSDT");
                    break;
                    
                case "3":
                    Console.WriteLine("🎯 Запуск теста WebSocket...");
                    Console.WriteLine("⚠️ Тест будет длиться 30 секунд...");
                    await tester.TestWebSocketAsync("ETHUSDT", 30);
                    break;
                    
                case "4":
                    Console.WriteLine("🎯 Запуск теста Integration...");
                    await tester.TestIntegrationAsync("ETHUSDT");
                    break;
                    
                case "5":
                    Console.WriteLine("🎯 Запуск всех тестов...");
                    await tester.RunAllTestsAsync("ETHUSDT");
                    break;
                    
                default:
                    Console.WriteLine("❌ Неверный выбор, запускаем все тесты...");
                    await tester.RunAllTestsAsync("ETHUSDT");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"💥 Критическая ошибка: {ex.Message}");
        }
        
        Console.WriteLine();
        Console.WriteLine("✅ Тест компонентов OBIZ-Score завершен");
    }
}