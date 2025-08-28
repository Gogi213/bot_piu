namespace Config
{
    public class TradingConfig
    {
        public string Symbol { get; set; } = "BIOUSDT";
        public string Side { get; set; } = "BUY";
        public decimal UsdAmount { get; set; } = 8.0m;
        public decimal TakeProfitPercent { get; set; } = 3.0m;
        public decimal StopLossPercent { get; set; } = 1.2m;
        public bool EnableBreakEven { get; set; } = true;
        public decimal BreakEvenActivationPercent { get; set; } = 0.6m;
        public decimal BreakEvenStopLossPercent { get; set; } = 0.2m;
        public decimal TickSize { get; set; } = 0.00001m;
        public double MonitorIntervalSeconds { get; set; } = 0.01;
    }
}