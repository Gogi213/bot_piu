using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Config
{
    public class TradingConfig
    {
        public string Symbol { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public decimal UsdAmount { get; set; }
        public decimal TakeProfitPercent { get; set; }
        public decimal StopLossPercent { get; set; }
        public bool EnableBreakEven { get; set; }
        public decimal BreakEvenActivationPercent { get; set; }
        public decimal BreakEvenStopLossPercent { get; set; }
        public decimal TickSize { get; set; }
        public decimal MonitorIntervalSeconds { get; set; }

        public static TradingConfig LoadFromConfiguration(IConfiguration configuration)
        {
            var tradingConfig = new TradingConfig();
            configuration.GetSection("Trading").Bind(tradingConfig);
            return tradingConfig;
        }
    }

    public class BackendConfig
    {
        public int Port { get; set; } = 5000;
        public decimal MinVolumeUsdt { get; set; } = 100000000;
        public decimal MinNatrPercent { get; set; } = 0.4m;
        public int NatrPeriods { get; set; } = 30;
        public int HistoryCandles { get; set; } = 50;
        public int UpdateIntervalMinutes { get; set; } = 5;
        public int ZScoreSmaPeriod { get; set; } = 17;
        public decimal ZScoreThreshold { get; set; } = 1.4m;
        public int StrategySmaPeriod { get; set; } = 30;
        
        // 15-секундный таймфрейм для торговли (обязательно должен быть true)
        public bool EnableFifteenSecondTrading { get; set; } = false;
        public int FifteenSecondWarmupCandles { get; set; } = 35;

                public static BackendConfig LoadFromConfiguration(IConfiguration configuration)
        {
            var backendConfig = new BackendConfig();
            configuration.GetSection("Backend").Bind(backendConfig);
            return backendConfig;
        }
    }

    /// <summary>
    /// Конфигурация автоматической торговли
    /// </summary>
    public class AutoTradingConfig
    {
        public int MaxConcurrentPositions { get; set; } = 2;
        public int MinTimeBetweenTradesMinutes { get; set; } = 15;
        public bool EnableAutoTrading { get; set; } = true;

        public static AutoTradingConfig LoadFromConfiguration(IConfiguration configuration)
        {
            var autoTradingConfig = new AutoTradingConfig();
            configuration.GetSection("AutoTrading").Bind(autoTradingConfig);
            return autoTradingConfig;
        }
    }

    /// <summary>
    /// Конфигурация стратегий торговли
    /// </summary>
    public class StrategyConfig
    {
        // Текущие стратегии (Z-Score + SMA)
        public bool EnableLegacyStrategies { get; set; } = true;
        
        // OBIZ-Score стратегия
        public bool EnableOBIZStrategy { get; set; } = false;
        public decimal OBIZWeight { get; set; } = 1.0m; // Вес OBIZ сигналов при комбинировании
        
        // Режим работы
        public StrategyMode Mode { get; set; } = StrategyMode.Legacy;
        
        public static StrategyConfig LoadFromConfiguration(IConfiguration configuration)
        {
            var strategyConfig = new StrategyConfig();
            configuration.GetSection("Strategy").Bind(strategyConfig);
            return strategyConfig;
        }
    }

    /// <summary>
    /// Режимы работы стратегий
    /// </summary>
    public enum StrategyMode
    {
        Legacy,      // Только старые стратегии (Z-Score + SMA)
        OBIZOnly,    // Только OBIZ-Score
        Combined     // Комбинированный режим
    }

    /// <summary>
    /// Конфигурация выбора монет для торговли
    /// </summary>
    public class CoinSelectionConfig
    {
        // Режим выбора монет
        public CoinSelectionMode Mode { get; set; } = CoinSelectionMode.Auto;
        
        // Список монет для ручного режима
        public List<string> ManualCoins { get; set; } = new List<string>();
        
        // Описание (игнорируется при загрузке)
        public string Description { get; set; } = string.Empty;

        public static CoinSelectionConfig LoadFromConfiguration(IConfiguration configuration)
        {
            var coinSelectionConfig = new CoinSelectionConfig();
            configuration.GetSection("CoinSelection").Bind(coinSelectionConfig);
            return coinSelectionConfig;
        }

        /// <summary>
        /// Валидация конфигурации
        /// </summary>
        public void Validate()
        {
            if (Mode == CoinSelectionMode.Manual && (ManualCoins == null || !ManualCoins.Any()))
            {
                throw new ArgumentException("В ручном режиме должен быть указан хотя бы один символ в ManualCoins");
            }

            if (Mode == CoinSelectionMode.Manual)
            {
                // Проверяем формат символов
                foreach (var symbol in ManualCoins)
                {
                    if (string.IsNullOrWhiteSpace(symbol) || !symbol.EndsWith("USDT"))
                    {
                        throw new ArgumentException($"Неверный формат символа: {symbol}. Ожидается формат типа BTCUSDT");
                    }
                }
            }
        }

        /// <summary>
        /// Получение списка символов для торговли
        /// </summary>
        public List<string> GetTradingSymbols(List<string> autoFilteredSymbols)
        {
            return Mode switch
            {
                CoinSelectionMode.Manual => ManualCoins.Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
                CoinSelectionMode.Auto => autoFilteredSymbols,
                _ => autoFilteredSymbols
            };
        }

        public override string ToString()
        {
            return Mode == CoinSelectionMode.Manual 
                ? $"Manual mode: {ManualCoins.Count} coins ({string.Join(", ", ManualCoins.Take(3))}{(ManualCoins.Count > 3 ? "..." : "")})"
                : "Auto mode: filtered by volume and volatility";
        }
    }

    /// <summary>
    /// Режимы выбора монет
    /// </summary>
    public enum CoinSelectionMode
    {
        Auto,    // Автоматический отбор по фильтрам (объем, NATR)
        Manual   // Ручной выбор из списка ManualCoins
    }
}