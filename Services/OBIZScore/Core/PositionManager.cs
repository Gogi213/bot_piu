using System;

namespace Services.OBIZScore.Core
{
    /// <summary>
    /// Управление текущей позицией для OBIZ-Score стратегии
    /// </summary>
    public class PositionManager
    {
        public bool IsOpen { get; private set; }
        public TradeDirection Direction { get; private set; }
        public decimal EntryPrice { get; private set; }
        public decimal TPPrice { get; private set; }
        public decimal SLPrice { get; private set; }
        public DateTime EntryTime { get; private set; }
        public bool IsPartialClosed { get; private set; }
        public decimal InitialQuantity { get; private set; }
        public decimal CurrentQuantity { get; private set; }
        public string Symbol { get; private set; } = string.Empty;

        /// <summary>
        /// Открывает новую позицию
        /// </summary>
        public void Open(OBIZSignal signal, decimal quantity, string symbol)
        {
            if (IsOpen)
                throw new InvalidOperationException("Position is already open");

            IsOpen = true;
            Direction = signal.Direction;
            EntryPrice = signal.EntryPrice;
            TPPrice = signal.TPPrice;
            SLPrice = signal.SLPrice;
            EntryTime = DateTime.UtcNow;
            IsPartialClosed = false;
            InitialQuantity = quantity;
            CurrentQuantity = quantity;
            Symbol = symbol;
        }

        /// <summary>
        /// Закрывает позицию полностью
        /// </summary>
        public void Close()
        {
            if (!IsOpen)
                throw new InvalidOperationException("No position to close");

            IsOpen = false;
            IsPartialClosed = false;
            CurrentQuantity = 0;
        }

        /// <summary>
        /// Частично закрывает позицию
        /// </summary>
        public void PartialClose(decimal percentage = 0.5m)
        {
            if (!IsOpen)
                throw new InvalidOperationException("No position to close");

            if (percentage <= 0 || percentage >= 1)
                throw new ArgumentException("Percentage must be between 0 and 1", nameof(percentage));

            CurrentQuantity *= (1 - percentage);
            IsPartialClosed = true;

            // Если количество стало слишком маленьким, закрываем полностью
            if (CurrentQuantity < InitialQuantity * 0.1m)
            {
                Close();
            }
        }

        /// <summary>
        /// Рассчитывает текущий PnL в процентах от максимального потенциала
        /// </summary>
        public decimal GetPnLRatio(decimal currentPrice)
        {
            if (!IsOpen) return 0;

            decimal pnl = Direction == TradeDirection.Buy 
                ? currentPrice - EntryPrice 
                : EntryPrice - currentPrice;

            decimal maxProfit = Math.Abs(TPPrice - EntryPrice);
            return maxProfit > 0 ? pnl / maxProfit : 0;
        }

        /// <summary>
        /// Рассчитывает абсолютный PnL
        /// </summary>
        public decimal GetAbsolutePnL(decimal currentPrice)
        {
            if (!IsOpen) return 0;

            decimal pnl = Direction == TradeDirection.Buy 
                ? currentPrice - EntryPrice 
                : EntryPrice - currentPrice;

            return pnl * CurrentQuantity;
        }

        /// <summary>
        /// Рассчитывает PnL в процентах от входной цены
        /// </summary>
        public decimal GetPnLPercent(decimal currentPrice)
        {
            if (!IsOpen || EntryPrice == 0) return 0;

            decimal pnl = Direction == TradeDirection.Buy 
                ? currentPrice - EntryPrice 
                : EntryPrice - currentPrice;

            return (pnl / EntryPrice) * 100;
        }

        /// <summary>
        /// Время удержания позиции в секундах
        /// </summary>
        public int GetHoldingTimeSeconds()
        {
            return IsOpen ? (int)(DateTime.UtcNow - EntryTime).TotalSeconds : 0;
        }

        /// <summary>
        /// Время удержания позиции в минутах
        /// </summary>
        public double GetHoldingTimeMinutes()
        {
            return IsOpen ? (DateTime.UtcNow - EntryTime).TotalMinutes : 0;
        }

        /// <summary>
        /// Проверяет, нужно ли закрыть позицию по TP
        /// </summary>
        public bool ShouldTakeProfit(decimal currentPrice)
        {
            if (!IsOpen) return false;

            return Direction == TradeDirection.Buy 
                ? currentPrice >= TPPrice 
                : currentPrice <= TPPrice;
        }

        /// <summary>
        /// Проверяет, нужно ли закрыть позицию по SL
        /// </summary>
        public bool ShouldStopLoss(decimal currentPrice)
        {
            if (!IsOpen) return false;

            return Direction == TradeDirection.Buy 
                ? currentPrice <= SLPrice 
                : currentPrice >= SLPrice;
        }

        /// <summary>
        /// Обновляет уровни TP/SL (для trailing stop или адаптивных уровней)
        /// </summary>
        public void UpdateLevels(decimal newTP, decimal newSL)
        {
            if (!IsOpen)
                throw new InvalidOperationException("No position to update");

            // Проверяем, что новые уровни логичны
            if (Direction == TradeDirection.Buy)
            {
                if (newTP <= EntryPrice || newSL >= EntryPrice)
                    throw new ArgumentException("Invalid TP/SL levels for BUY position");
            }
            else
            {
                if (newTP >= EntryPrice || newSL <= EntryPrice)
                    throw new ArgumentException("Invalid TP/SL levels for SELL position");
            }

            TPPrice = newTP;
            SLPrice = newSL;
        }

        /// <summary>
        /// Информация о позиции для логирования
        /// </summary>
        public string GetPositionInfo(decimal currentPrice)
        {
            if (!IsOpen) return "No open position";

            var pnlPercent = GetPnLPercent(currentPrice);
            var holdingTime = GetHoldingTimeMinutes();
            
            return $"{Symbol} {Direction} | Entry: {EntryPrice:F4} | Current: {currentPrice:F4} | " +
                   $"PnL: {pnlPercent:F2}% | Time: {holdingTime:F1}m | " +
                   $"TP: {TPPrice:F4} | SL: {SLPrice:F4}";
        }
    }
}
