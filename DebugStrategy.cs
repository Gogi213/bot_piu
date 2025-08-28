using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Config;
using Models;
using Services;

public class DebugStrategy
{
    public static async Task TestStrategyDebugAsync()
    {
        Console.WriteLine("🧠 DEBUG СТРАТЕГИИ");
        Console.WriteLine("==================");
        
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

            var configuration = new ConfigurationBuilder()
                .SetBasePath(System.IO.Directory.GetCurrentDirectory())
                .AddJsonFile("config.json", optional: false, reloadOnChange: true)
                .Build();

            var backendConfig = BackendConfig.LoadFromConfiguration(configuration);

            // Создаем клиенты и сервисы
            var restClient = new BinanceRestClient(options =>
            {
                options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            });

            var dataStorage = new DataStorageService();
            var binanceService = new BinanceDataService(restClient, backendConfig);
            var universeService = new UniverseUpdateService(binanceService, dataStorage, backendConfig);
            var strategyService = new TradingStrategyService(backendConfig);

            Console.WriteLine("📊 Подготовка данных...");
            var result = await universeService.UpdateUniverseAsync();
            if (!result.Success)
            {
                Console.WriteLine($"❌ Ошибка загрузки данных: {result.ErrorMessage}");
                return;
            }

            var filteredCoins = dataStorage.GetFilteredCoins(backendConfig.MinVolumeUsdt, backendConfig.MinNatrPercent);
            Console.WriteLine($"✅ Подготовлено {filteredCoins.Count} монет для анализа");

            // Берем первую монету и анализируем детально
            if (filteredCoins.Count > 0)
            {
                var coin = filteredCoins.First();
                Console.WriteLine($"\n🔍 ДЕТАЛЬНЫЙ АНАЛИЗ: {coin.Symbol}");
                Console.WriteLine($"   💰 Цена: {coin.CurrentPrice:F6}");
                Console.WriteLine($"   📊 NATR: {coin.Natr:F2}%");
                Console.WriteLine($"   🕐 Свечей: {coin.RecentCandles.Count}");
                
                if (coin.RecentCandles.Count >= 20)
                {
                    // Z-Score стратегия
                    var (zScore, zScoreSignal) = TechnicalAnalysisService.CalculateZScoreSma(
                        coin.RecentCandles, 
                        backendConfig.ZScoreSmaPeriod, 
                        backendConfig.ZScoreThreshold);
                    
                    Console.WriteLine($"   🎯 Z-Score: {zScore:F4} → {zScoreSignal}");
                    Console.WriteLine($"   📈 Z-Threshold: {backendConfig.ZScoreThreshold}");
                    
                    // SMA стратегия
                    var (sma, smaSignal) = TechnicalAnalysisService.CalculateSmaStrategy(
                        coin.RecentCandles, 
                        backendConfig.StrategySmaPeriod);
                    
                    Console.WriteLine($"   📊 SMA: {sma:F6} → {smaSignal}");
                    Console.WriteLine($"   📈 Цена vs SMA: {(coin.CurrentPrice > sma ? "ВЫШЕ" : "НИЖЕ")}");
                    
                    // Финальный результат стратегии
                    var strategyResult = strategyService.AnalyzeCoin(coin);
                    Console.WriteLine($"   🏆 ФИНАЛЬНЫЙ СИГНАЛ: {strategyResult.FinalSignal}");
                    Console.WriteLine($"   💭 Причина: {strategyResult.Reason}");
                }
                else
                {
                    Console.WriteLine($"   ❌ Недостаточно свечей для анализа");
                }
            }
            else
            {
                Console.WriteLine("❌ Нет монет для анализа");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Ошибка: {ex.Message}");
            Console.WriteLine($"Stack: {ex.StackTrace}");
        }
        
        Console.WriteLine("\nНажмите любую клавишу...");
        Console.ReadKey();
    }
    
    private static void LoadEnvFile()
    {
        var envFile = ".env";
        if (System.IO.File.Exists(envFile))
        {
            foreach (var line in System.IO.File.ReadAllLines(envFile))
            {
                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
                    }
                }
            }
        }
    }
}
