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
        public decimal MinVolumeUsdt { get; set; } = 30000000;
        public decimal MinNatrPercent { get; set; } = 0.4m;
        public int NatrPeriods { get; set; } = 30;
        public int HistoryCandles { get; set; } = 50;
        public int UpdateIntervalMinutes { get; set; } = 5;
        public int ZScoreSmaPeriod { get; set; } = 17;
        public decimal ZScoreThreshold { get; set; } = 1.4m;
        public int StrategySmaPeriod { get; set; } = 30;

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
        public decimal RiskPercentPerTrade { get; set; } = 1.0m;
        public decimal MinSignalStrength { get; set; } = 1.2m;
        public bool EnableAutoTrading { get; set; } = true;

        public static AutoTradingConfig LoadFromConfiguration(IConfiguration configuration)
        {
            var autoTradingConfig = new AutoTradingConfig();
            configuration.GetSection("AutoTrading").Bind(autoTradingConfig);
            return autoTradingConfig;
        }
    }
}