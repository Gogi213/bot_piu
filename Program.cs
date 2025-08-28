using System;
using System.Threading.Tasks;
using System.IO;
using Binance.Net;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using Trading;
using Config;

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

    static async Task Main()
    {
        LoadEnvFile();

        // Загружаем ключи из .env
        var apiKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY");
        var apiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET");

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            return;
        }

        // Загружаем конфигурацию
        TradingConfig tradingConfig;
        try
        {
            tradingConfig = ConfigManager.LoadTradingConfig();
        }
        catch (Exception)
        {
            return;
        }

        // Создаем клиенты Binance
        var restClient = new BinanceRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
        });

        var socketClient = new BinanceSocketClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
        });

        // Создаем торговый модуль и выполняем торговлю
        var tradingModule = new TradingModule(restClient, socketClient, tradingConfig);
        await tradingModule.ExecuteTradeAsync();
    }
}