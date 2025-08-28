using System;
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
                    
                default:
                    Console.WriteLine("🚀 ДОСТУПНЫЕ КОМАНДЫ ТЕСТИРОВАНИЯ:");
                    Console.WriteLine("==================================");
                    Console.WriteLine("  test-pool       - Тест сбора и фильтрации пула монет");
                    Console.WriteLine("  test-websocket  - Тест WebSocket real-time данных");
                    Console.WriteLine("  test-strategy   - Тест торговой стратегии и сигналов");
                    Console.WriteLine("  test-hft        - Тест псевдо-HFT системы");
                    Console.WriteLine("  test-auto       - Тест автоматической торговли");
                    Console.WriteLine("  test-all        - Запуск всех тестов последовательно");
                    Console.WriteLine();
                    Console.WriteLine("💡 Для обычной торговли запустите без параметров");
                    return;
            }
        }

        // АВТОНОМНЫЙ РЕЖИМ ТОРГОВЛИ с автовосстановлением
        LoadEnvFile();
        
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 СИСТЕМА ЗАПУЩЕНА");

        // Загружаем ключи из .env
        var apiKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY");
        var apiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET");

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
}