using System;

namespace Services.OBIZScore.Core
{
    /// <summary>
    /// Структура данных тика с Order Book информацией
    /// </summary>
    public struct TickData
    {
        public DateTime Timestamp { get; set; }
        public decimal Price { get; set; }
        public long Volume { get; set; }
        public decimal BestBid { get; set; }
        public decimal BestAsk { get; set; }
        public long BidSize { get; set; }
        public long AskSize { get; set; }
        public OrderBookLevel[] Bids { get; set; } // Топ 10 уровней
        public OrderBookLevel[] Asks { get; set; } // Топ 10 уровней
        public TradeDirection Direction { get; set; } // Buy/Sell агрессор
    }

    /// <summary>
    /// Уровень Order Book'а
    /// </summary>
    public struct OrderBookLevel
    {
        public decimal Price { get; set; }
        public long Size { get; set; }
    }

    /// <summary>
    /// Направление торговли
    /// </summary>
    public enum TradeDirection 
    { 
        Buy, 
        Sell, 
        Unknown 
    }

    /// <summary>
    /// Торговый сигнал от OBIZ-Score стратегии
    /// </summary>
    public struct OBIZSignal
    {
        public TradeDirection Direction { get; set; }
        public SignalConfidence Confidence { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal TPPrice { get; set; }
        public decimal SLPrice { get; set; }
        public DateTime Timestamp { get; set; }
        public decimal OBIZScore { get; set; }
        public decimal ActivityScore { get; set; }
        public decimal EfficiencyRatio { get; set; }
        public decimal VWAPDeviation { get; set; }
        public MarketRegime Regime { get; set; }
    }

    /// <summary>
    /// Уровень уверенности в сигнале
    /// </summary>
    public enum SignalConfidence 
    { 
        Low, 
        Medium, 
        High 
    }

    /// <summary>
    /// Режим рынка
    /// </summary>
    public enum MarketRegime 
    { 
        Choppy,    // Боковик - mean reversion
        Trending,  // Тренд - momentum
        Mixed      // Смешанный - консервативный подход
    }

    /// <summary>
    /// Результат торгового решения
    /// </summary>
    public enum TradingAction 
    { 
        NoAction, 
        OpenPosition, 
        ClosePosition, 
        PartialClose 
    }

    /// <summary>
    /// Решение торговой стратегии
    /// </summary>
    public struct TradingDecision
    {
        public TradingAction Action { get; set; }
        public OBIZSignal? Signal { get; set; }
        public decimal Percentage { get; set; }
        
        public static TradingDecision NoAction => new() 
        { 
            Action = TradingAction.NoAction 
        };
    }

    /// <summary>
    /// Статистики OBIZ-Score стратегии
    /// </summary>
    public class OBIZStrategyStats
    {
        public decimal CurrentOBIZScore { get; set; }
        public decimal CurrentActivityScore { get; set; }
        public decimal CurrentEfficiencyRatio { get; set; }
        public decimal CurrentVWAPDeviation { get; set; }
        public MarketRegime CurrentRegime { get; set; }
        public int TicksProcessed { get; set; }
        public DateTime LastUpdate { get; set; }
        public bool HasSufficientData { get; set; }
    }
}
