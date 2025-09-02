using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Services.OBIZScore.Core;
using Services.OBIZScore.Config;
using Models;
using Config;

namespace Services.OBIZScore
{
    /// <summary>
    /// Расширенный менеджер позиций для OBIZ-Score стратегии
    /// Поддерживает множественные позиции и продвинутое управление рисками
    /// </summary>
    public class OBIZPositionManager
    {
        private readonly ConcurrentDictionary<string, PositionManager> _positions;
        private readonly OBIZStrategyConfig _config;
        private readonly AutoTradingConfig _autoConfig;
        private readonly TradingConfig _tradingConfig;


        public OBIZPositionManager(
            OBIZStrategyConfig config,
            AutoTradingConfig autoConfig,
            TradingConfig tradingConfig)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _autoConfig = autoConfig ?? throw new ArgumentNullException(nameof(autoConfig));
            _tradingConfig = tradingConfig ?? throw new ArgumentNullException(nameof(tradingConfig));
            
            _positions = new ConcurrentDictionary<string, PositionManager>();
        }

        /// <summary>
        /// Проверка возможности открытия новой позиции
        /// </summary>
        public bool CanOpenPosition(string symbol, OBIZSignal signal)
        {
            try
            {
                // 1. Проверка общих ограничений
                if (!_autoConfig.EnableAutoTrading)
                {
                    LogInfo("Auto trading is disabled");
                    return false;
                }

                // 2. Проверка максимального количества позиций
                int openPositions = _positions.Values.Count(p => p.IsOpen);
                if (openPositions >= _autoConfig.MaxConcurrentPositions)
                {
                    LogInfo($"Max concurrent positions reached: {openPositions}/{_autoConfig.MaxConcurrentPositions}");
                    return false;
                }

                // 3. Проверка позиции по символу
                if (_positions.TryGetValue(symbol, out var existingPosition) && existingPosition.IsOpen)
                {
                    LogInfo($"Position already open for {symbol}");
                    return false;
                }

                // 4. Убрано: IsTradeTimeAllowed - неясно зачем

                // 5. Убрано: IsSignalQualityGood - залупа какая-то

                // 6. Проверка риск-менеджмента
                if (!IsRiskAcceptable(signal))
                {
                    LogInfo($"Risk too high for {symbol}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogError($"Error checking position opening for {symbol}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Открытие новой позиции
        /// </summary>
        public async Task<PositionOpenResult> OpenPositionAsync(string symbol, OBIZSignal signal)
        {
            var result = new PositionOpenResult
            {
                Symbol = symbol,
                Success = false,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                if (!CanOpenPosition(symbol, signal))
                {
                    result.ErrorMessage = "Position opening not allowed";
                    return result;
                }

                // Расчет размера позиции
                decimal positionSize = CalculatePositionSize(signal);
                if (positionSize <= 0)
                {
                    result.ErrorMessage = "Invalid position size calculated";
                    return result;
                }

                // Создание или получение менеджера позиции
                var positionManager = _positions.GetOrAdd(symbol, _ => new PositionManager());
                
                // Открытие позиции
                positionManager.Open(signal, positionSize, symbol);

                // Убрано: регистрация времени сделки

                result.Success = true;
                result.PositionSize = positionSize;
                result.EntryPrice = signal.EntryPrice;
                result.TPPrice = signal.TPPrice;
                result.SLPrice = signal.SLPrice;
                result.Direction = signal.Direction;
                result.Confidence = signal.Confidence;

                LogInfo($"Position opened: {positionManager.GetPositionInfo(signal.EntryPrice)}");
                
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                LogError($"Error opening position for {symbol}: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Закрытие позиции
        /// </summary>
        public async Task<PositionCloseResult> ClosePositionAsync(string symbol, decimal currentPrice, string reason = "Manual close")
        {
            var result = new PositionCloseResult
            {
                Symbol = symbol,
                Success = false,
                Timestamp = DateTime.UtcNow,
                Reason = reason
            };

            try
            {
                if (!_positions.TryGetValue(symbol, out var positionManager) || !positionManager.IsOpen)
                {
                    result.ErrorMessage = "No open position found";
                    return result;
                }

                // Сохраняем информацию о позиции
                result.EntryPrice = positionManager.EntryPrice;
                result.ExitPrice = currentPrice;
                result.Direction = positionManager.Direction;
                result.HoldingTimeMinutes = positionManager.GetHoldingTimeMinutes();
                result.PnLPercent = positionManager.GetPnLPercent(currentPrice);
                result.PnLAbsolute = positionManager.GetAbsolutePnL(currentPrice);

                // Закрытие позиции
                positionManager.Close();

                result.Success = true;
                LogInfo($"Position closed: {symbol} | PnL: {result.PnLPercent:F2}% | Time: {result.HoldingTimeMinutes:F1}m | Reason: {reason}");
                
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                LogError($"Error closing position for {symbol}: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Частичное закрытие позиции
        /// </summary>
        public async Task<PositionCloseResult> PartialClosePositionAsync(string symbol, decimal currentPrice, decimal percentage = 0.5m)
        {
            var result = new PositionCloseResult
            {
                Symbol = symbol,
                Success = false,
                Timestamp = DateTime.UtcNow,
                Reason = $"Partial close {percentage:P0}"
            };

            try
            {
                if (!_positions.TryGetValue(symbol, out var positionManager) || !positionManager.IsOpen)
                {
                    result.ErrorMessage = "No open position found";
                    return result;
                }

                // Сохраняем информацию до частичного закрытия
                result.EntryPrice = positionManager.EntryPrice;
                result.ExitPrice = currentPrice;
                result.Direction = positionManager.Direction;
                result.HoldingTimeMinutes = positionManager.GetHoldingTimeMinutes();
                result.PnLPercent = positionManager.GetPnLPercent(currentPrice);
                result.PnLAbsolute = positionManager.GetAbsolutePnL(currentPrice) * percentage;

                // Частичное закрытие
                positionManager.PartialClose(percentage);

                result.Success = true;
                LogInfo($"Position partially closed: {symbol} | {percentage:P0} | PnL: {result.PnLPercent:F2}%");
                
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                LogError($"Error partial closing position for {symbol}: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Обновление всех открытых позиций
        /// </summary>
        public async Task<List<PositionUpdateResult>> UpdateAllPositionsAsync(Dictionary<string, decimal> currentPrices)
        {
            var results = new List<PositionUpdateResult>();

            var openPositions = _positions
                .Where(kvp => kvp.Value.IsOpen)
                .ToList();

            foreach (var (symbol, positionManager) in openPositions)
            {
                if (!currentPrices.TryGetValue(symbol, out decimal currentPrice))
                    continue;

                var updateResult = await UpdatePositionAsync(symbol, positionManager, currentPrice);
                results.Add(updateResult);
            }

            return results;
        }

        /// <summary>
        /// Обновление конкретной позиции
        /// </summary>
        private async Task<PositionUpdateResult> UpdatePositionAsync(string symbol, PositionManager positionManager, decimal currentPrice)
        {
            var result = new PositionUpdateResult
            {
                Symbol = symbol,
                CurrentPrice = currentPrice,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // 1. Проверка Stop Loss
                if (positionManager.ShouldStopLoss(currentPrice))
                {
                    var closeResult = await ClosePositionAsync(symbol, currentPrice, "Stop Loss");
                    result.Action = "STOP_LOSS";
                    result.ActionTaken = closeResult.Success;
                    return result;
                }

                // 2. Проверка Take Profit
                if (positionManager.ShouldTakeProfit(currentPrice))
                {
                    var closeResult = await ClosePositionAsync(symbol, currentPrice, "Take Profit");
                    result.Action = "TAKE_PROFIT";
                    result.ActionTaken = closeResult.Success;
                    return result;
                }

                // 3. Проверка частичного закрытия
                decimal pnlRatio = positionManager.GetPnLRatio(currentPrice);
                if (pnlRatio >= _config.PartialCloseRatio && !positionManager.IsPartialClosed)
                {
                    var partialResult = await PartialClosePositionAsync(symbol, currentPrice, 0.5m);
                    result.Action = "PARTIAL_CLOSE";
                    result.ActionTaken = partialResult.Success;
                    return result;
                }

                // 4. Проверка времени удержания
                if (positionManager.GetHoldingTimeSeconds() > _config.MaxHoldTimeSeconds)
                {
                    var closeResult = await ClosePositionAsync(symbol, currentPrice, "Max holding time");
                    result.Action = "TIME_EXIT";
                    result.ActionTaken = closeResult.Success;
                    return result;
                }

                // 5. Обновление информации о позиции
                result.Action = "MONITOR";
                result.ActionTaken = true;
                result.CurrentPnL = positionManager.GetPnLPercent(currentPrice);
                result.HoldingTime = positionManager.GetHoldingTimeMinutes();

                return result;
            }
            catch (Exception ex)
            {
                result.Action = "ERROR";
                result.ActionTaken = false;
                result.ErrorMessage = ex.Message;
                LogError($"Error updating position for {symbol}: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Расчет размера позиции на основе риск-менеджмента
        /// </summary>
        private decimal CalculatePositionSize(OBIZSignal signal)
        {
            try
            {
                // Расчет риска на сделку
                decimal riskAmount = _tradingConfig.UsdAmount * _config.MaxRiskPerTrade;
                
                // Расчет размера позиции исходя из Stop Loss
                decimal stopLossDistance = Math.Abs(signal.EntryPrice - signal.SLPrice);
                if (stopLossDistance == 0) return 0;
                
                decimal positionSize = riskAmount / stopLossDistance;
                
                // Ограничиваем максимальным размером
                decimal maxPositionSize = _tradingConfig.UsdAmount / signal.EntryPrice;
                positionSize = Math.Min(positionSize, maxPositionSize);
                
                // Корректировка по уверенности сигнала
                decimal confidenceMultiplier = signal.Confidence switch
                {
                    SignalConfidence.High => 1.0m,
                    SignalConfidence.Medium => 0.7m,
                    SignalConfidence.Low => 0.5m,
                    _ => 0.3m
                };
                
                positionSize *= confidenceMultiplier;
                
                return Math.Max(0, positionSize);
            }
            catch (Exception ex)
            {
                LogError($"Error calculating position size: {ex.Message}");
                return 0;
            }
        }



        /// <summary>
        /// Проверка приемлемости риска
        /// </summary>
        private bool IsRiskAcceptable(OBIZSignal signal)
        {
            decimal stopLossPercent = Math.Abs(signal.EntryPrice - signal.SLPrice) / signal.EntryPrice;
            
            // Максимальный риск на сделку не должен превышать настройки
            return stopLossPercent <= _config.MaxRiskPerTrade * 2; // Удваиваем для гибкости
        }





        /// <summary>
        /// Получение информации о всех позициях
        /// </summary>
        public List<PositionInfo> GetAllPositions(Dictionary<string, decimal> currentPrices)
        {
            var positions = new List<PositionInfo>();

            foreach (var (symbol, positionManager) in _positions)
            {
                if (!positionManager.IsOpen)
                    continue;

                decimal currentPrice = currentPrices.GetValueOrDefault(symbol, positionManager.EntryPrice);
                
                positions.Add(new PositionInfo
                {
                    Symbol = symbol,
                    Direction = positionManager.Direction,
                    EntryPrice = positionManager.EntryPrice,
                    CurrentPrice = currentPrice,
                    TPPrice = positionManager.TPPrice,
                    SLPrice = positionManager.SLPrice,
                    PnLPercent = positionManager.GetPnLPercent(currentPrice),
                    PnLAbsolute = positionManager.GetAbsolutePnL(currentPrice),
                    HoldingTimeMinutes = positionManager.GetHoldingTimeMinutes(),
                    IsPartialClosed = positionManager.IsPartialClosed,
                    CurrentQuantity = positionManager.CurrentQuantity,
                    InitialQuantity = positionManager.InitialQuantity
                });
            }

            return positions.OrderByDescending(p => Math.Abs(p.PnLPercent)).ToList();
        }

        /// <summary>
        /// Статистика позиций
        /// </summary>
        public PositionStatistics GetStatistics()
        {
            var openPositions = _positions.Values.Where(p => p.IsOpen).ToList();
            
            return new PositionStatistics
            {
                TotalOpenPositions = openPositions.Count,
                MaxAllowedPositions = _autoConfig.MaxConcurrentPositions,
                LongPositions = openPositions.Count(p => p.Direction == TradeDirection.Buy),
                ShortPositions = openPositions.Count(p => p.Direction == TradeDirection.Sell),
                PartiallyClosedPositions = openPositions.Count(p => p.IsPartialClosed),
                AverageHoldingTimeMinutes = openPositions.Any() ? 
                    openPositions.Average(p => p.GetHoldingTimeMinutes()) : 0
            };
        }

        private void LogInfo(string message)
        {
            if (_config.EnableDetailedLogging)
            {
                OBIZJsonLogger.Log("DEBUG", "OBIZ_POSITION_MANAGER", message);
            }
        }

        private void LogError(string message)
        {
            OBIZJsonLogger.Log("ERROR", "OBIZ_POSITION_MANAGER", message);
        }
    }

    // Вспомогательные классы для результатов операций
    public class PositionOpenResult
    {
        public string Symbol { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public decimal PositionSize { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal TPPrice { get; set; }
        public decimal SLPrice { get; set; }
        public TradeDirection Direction { get; set; }
        public SignalConfidence Confidence { get; set; }
    }

    public class PositionCloseResult
    {
        public string Symbol { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public TradeDirection Direction { get; set; }
        public double HoldingTimeMinutes { get; set; }
        public decimal PnLPercent { get; set; }
        public decimal PnLAbsolute { get; set; }
    }

    public class PositionUpdateResult
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal CurrentPrice { get; set; }
        public DateTime Timestamp { get; set; }
        public string Action { get; set; } = string.Empty;
        public bool ActionTaken { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public decimal CurrentPnL { get; set; }
        public double HoldingTime { get; set; }
    }

    public class PositionInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public TradeDirection Direction { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal TPPrice { get; set; }
        public decimal SLPrice { get; set; }
        public decimal PnLPercent { get; set; }
        public decimal PnLAbsolute { get; set; }
        public double HoldingTimeMinutes { get; set; }
        public bool IsPartialClosed { get; set; }
        public decimal CurrentQuantity { get; set; }
        public decimal InitialQuantity { get; set; }
    }

    public class PositionStatistics
    {
        public int TotalOpenPositions { get; set; }
        public int MaxAllowedPositions { get; set; }
        public int LongPositions { get; set; }
        public int ShortPositions { get; set; }
        public int PartiallyClosedPositions { get; set; }
        public double AverageHoldingTimeMinutes { get; set; }
    }
}
