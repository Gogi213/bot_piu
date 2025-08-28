using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Config
{
    public static class ConfigManager
    {
        public static TradingConfig LoadTradingConfig(string configPath = "config.json")
        {
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException($"Configuration file not found: {configPath}");
            }

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<ConfigRoot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            });

            return config?.Trading ?? new TradingConfig();
        }
    }

    public class ConfigRoot
    {
        public TradingConfig? Trading { get; set; }
    }
}