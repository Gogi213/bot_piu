using System;
using System.Collections.Generic;

namespace Models
{
    public class CoinData
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal Volume24h { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal? Natr { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<CandleData> RecentCandles { get; set; } = new List<CandleData>();
        
        // Жизненный цикл монеты
        public DateTime FirstAddedTime { get; set; } = DateTime.UtcNow;
        public DateTime LastPassedFiltersTime { get; set; } = DateTime.UtcNow;
        public int CyclesInPool { get; set; } = 1;
        public bool PassedCurrentFilters { get; set; } = true;
        public CoinLifecycleStatus Status { get; set; } = CoinLifecycleStatus.New;
    }

    public enum CoinLifecycleStatus
    {
        New,        // Новая монета, только добавлена
        Stable,     // Стабильная монета, прошла несколько циклов
        Warning,    // Не прошла фильтры, но еще в пуле
        Removing    // Помечена к удалению
    }

    public class CandleData
    {
        public DateTime OpenTime { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
    }

    public class TradingSignal
    {
        public string Symbol { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty; // BUY, SELL, FLAT
        public decimal CurrentPrice { get; set; }
        public decimal ZScore { get; set; }
        public decimal Natr { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class EngineStatus
    {
        public bool IsRunning { get; set; }
        public DateTime? StartTime { get; set; }
        public int ActiveSignals { get; set; }
        public int TotalCoins { get; set; }
        public DateTime? LastUpdate { get; set; }
    }
}
