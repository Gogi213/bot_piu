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
}