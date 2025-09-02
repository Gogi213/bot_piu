using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Services.OBIZScore.Core;
using Services.OBIZScore.Config;
using Services;
using Models;

namespace Services.OBIZScore
{
    /// <summary>
    /// OBIZ-Score стратегия - анализ дисбаланса Order Book с Z-Score нормализацией
    /// Адаптирована для интеграции с существующей архитектурой бота
    /// </summary>
    public partial class OBIZScoreStrategy
    {
        private readonly OBIZStrategyConfig _config;
        
        // Состояние стратегии
        private readonly CircularBuffer<TickData> _tickHistory;
        private readonly CircularBuffer<decimal> _priceHistory;
        private readonly CircularBuffer<long> _volumeHistory;
        private readonly CircularBuffer<decimal> _imbalanceHistory;
        
        // Статистики
        private readonly RollingStatistics _imbalanceStats;
        private readonly RollingStatistics _activityStats;
        
        // VWAP расчеты
        private decimal _currentVWAP;
        private long _vwapVolume;
        private decimal _vwapSum;
        
        // Текущие метрики
        private decimal _lastOBIZScore;
        private decimal _lastActivityScore;
        private decimal _lastEfficiencyRatio;
        private decimal _lastVWAPDeviation;
        private MarketRegime _currentRegime;
        
        // Управление позицией
        private readonly PositionManager _positionManager;
        

        
        // Статистики работы
        private int _ticksProcessed;
        private DateTime _lastUpdate;
        private readonly List<OBIZSignal> _signalHistory;

        public OBIZScoreStrategy(OBIZStrategyConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.Validate();
            
            // Инициализация буферов
            _tickHistory = new CircularBuffer<TickData>(_config.MaxHistorySize);
            _priceHistory = new CircularBuffer<decimal>(_config.MaxHistorySize);
            _volumeHistory = new CircularBuffer<long>(_config.MaxHistorySize);
            _imbalanceHistory = new CircularBuffer<decimal>(_config.MaxHistorySize);
            
            // Инициализация статистик
            _imbalanceStats = new RollingStatistics(_config.ZScoreWindow);
            _activityStats = new RollingStatistics(_config.ActivityWindow);
            
            // Управление позицией
            _positionManager = new PositionManager();
            
            // История сигналов
            _signalHistory = new List<OBIZSignal>();
            
            _lastUpdate = DateTime.UtcNow;
            
            LogInfo($"OBIZ Strategy initialized: {_config}");
        }

        /// <summary>
        /// Основной метод обработки тика - входная точка стратегии
        /// </summary>
        public async Task<TradingDecision> ProcessTickAsync(TickData tick, string symbol)
        {
            try
            {
                _ticksProcessed++;
                _lastUpdate = DateTime.UtcNow;
                
                LogDebug($"🔄 {symbol} | TICK #{_ticksProcessed} | Price: {tick.Price:F4} | Volume: {tick.Volume}");
                
                // 1. Обновление истории данных
                UpdateHistory(tick);
                LogDebug($"📊 {symbol} | HISTORY | Buffer size: {_tickHistory.Count}/{_config.MaxHistorySize}");
                
                // 2. Расчет всех метрик
                UpdateMetrics(tick);
                LogDebug($"🧠 {symbol} | METRICS", new Dictionary<string, object>
                {
                    ["symbol"] = symbol,
                    ["obizScore"] = _lastOBIZScore,
                    ["activityScore"] = _lastActivityScore,
                    ["efficiencyRatio"] = _lastEfficiencyRatio,
                    ["vwapDeviation"] = _lastVWAPDeviation,
                    ["marketRegime"] = _currentRegime.ToString(),
                    ["ticksProcessed"] = _ticksProcessed
                });
                
                // 3. Проверка текущей позиции
                if (_positionManager.IsOpen)
                {
                    LogDebug($"💼 {symbol} | POSITION OPEN | Managing existing position");
                    return await ManageOpenPositionAsync(tick, symbol);
                }
                
                // 4. Поиск новых сигналов на вход
                LogDebug($"🎯 {symbol} | SIGNAL CHECK | Looking for entry signals...");
                var signal = await CheckEntryConditionsAsync(tick, symbol);
                if (signal.HasValue)
                {
                    LogInfo($"🚀 {symbol} | SIGNAL GENERATED", new Dictionary<string, object>
                    {
                        ["symbol"] = symbol,
                        ["direction"] = signal.Value.Direction.ToString(),
                        ["confidence"] = signal.Value.Confidence.ToString(),
                        ["obizScore"] = signal.Value.OBIZScore,
                        ["entryPrice"] = signal.Value.EntryPrice,
                        ["takeProfitPrice"] = signal.Value.TPPrice,
                        ["stopLossPrice"] = signal.Value.SLPrice,
                        ["timestamp"] = DateTime.UtcNow
                    });
                    return new TradingDecision 
                    { 
                        Action = TradingAction.OpenPosition,
                        Signal = signal.Value 
                    };
                }
                else
                {
                    LogDebug($"❌ {symbol} | NO SIGNAL | No entry conditions met");
                }
                
                return TradingDecision.NoAction;
            }
            catch (Exception ex)
            {
                LogError($"Error processing tick for {symbol}: {ex.Message}");
                return TradingDecision.NoAction;
            }
        }

        /// <summary>
        /// Обновление истории данных и VWAP
        /// </summary>
        private void UpdateHistory(TickData tick)
        {
            _tickHistory.Add(tick);
            
            // Расчет взвешенной средней цены для уменьшения шума
            decimal weightedMidPrice = CalculateWeightedMidPrice(tick);
            _priceHistory.Add(weightedMidPrice);
            _volumeHistory.Add(tick.Volume);
            
            // Инкрементальное обновление VWAP
            _vwapSum += weightedMidPrice * tick.Volume;
            _vwapVolume += tick.Volume;
            
            if (_vwapVolume > 0)
            {
                _currentVWAP = _vwapSum / _vwapVolume;
            }
            
            // Периодический сброс VWAP для адаптации к изменяющимся условиям
            if (_ticksProcessed % _config.VWAPResetPeriod == 0)
            {
                _vwapSum = 0;
                _vwapVolume = 0;
            }
        }

        /// <summary>
        /// Расчет всех метрик стратегии
        /// </summary>
        private void UpdateMetrics(TickData tick)
        {
            if (_tickHistory.Count < _config.MinHistoryForCalculation)
                return;
                
            // 1. Расчет OBI Z-Score
            _lastOBIZScore = CalculateOBIZScore(tick);
            
            // 2. Расчет Activity Score
            _lastActivityScore = CalculateActivityScore();
            
            // 3. Расчет Efficiency Ratio
            _lastEfficiencyRatio = CalculateEfficiencyRatio();
            
            // 4. Расчет VWAP Deviation
            _lastVWAPDeviation = CalculateVWAPDeviation(tick);
            
            // 5. Определение режима рынка
            _currentRegime = DetermineMarketRegime();
            
            // Обновление статистик
            _imbalanceStats.Add(_lastOBIZScore);
            _activityStats.Add(_lastActivityScore);
        }

        /// <summary>
        /// Расчет Order Book Imbalance Z-Score
        /// </summary>
        private decimal CalculateOBIZScore(TickData tick)
        {
            // Проверяем наличие Order Book данных
            if (tick.Bids?.Length < _config.OrderBookDepth || 
                tick.Asks?.Length < _config.OrderBookDepth)
            {
                return 0;
            }
            
            // Суммируем объемы на N уровнях
            int levels = Math.Min(_config.OrderBookDepth, tick.Bids.Length);
            levels = Math.Min(levels, tick.Asks.Length);
            
            long buyVolume = tick.Bids.Take(levels).Sum(b => b.Size);
            long sellVolume = tick.Asks.Take(levels).Sum(a => a.Size);
            long totalVolume = buyVolume + sellVolume;
            
            if (totalVolume == 0) return 0;
            
            // Order Book Imbalance: (Buy - Sell) / Total
            decimal imbalance = (decimal)(buyVolume - sellVolume) / totalVolume;
            _imbalanceHistory.Add(imbalance);
            
            // Z-Score нормализация
            if (_imbalanceHistory.Count < _config.ZScoreWindow)
                return 0;
                
            var recentImbalances = _imbalanceHistory.TakeLast(_config.ZScoreWindow);
            decimal mean = recentImbalances.Average();
            decimal stdDev = CalculateStandardDeviation(recentImbalances, mean);
            
            return stdDev > _config.MinVolatility ? (imbalance - mean) / stdDev : 0;
        }

        /// <summary>
        /// Расчет Activity Score (Realized Volatility * Volume)
        /// </summary>
        private decimal CalculateActivityScore()
        {
            int window = Math.Min(_config.ActivityWindow, _priceHistory.Count);
            if (window < 10) return 0;
            
            var recentPrices = _priceHistory.TakeLast(window).ToArray();
            
            // Realized Volatility
            decimal realizedVol = 0;
            for (int i = 1; i < recentPrices.Length; i++)
            {
                realizedVol += Math.Abs(recentPrices[i] - recentPrices[i-1]);
            }
            
            // Суммарный объем за период
            long totalVolume = _volumeHistory.TakeLast(window).Sum();
            
            // Activity Score = RV * Volume
            return realizedVol * totalVolume;
        }

        /// <summary>
        /// Расчет Efficiency Ratio (чистое движение / общее движение)
        /// </summary>
        private decimal CalculateEfficiencyRatio()
        {
            int window = Math.Min(_config.EfficiencyWindow, _priceHistory.Count);
            if (window < 10) return 0.5m; // Нейтральное значение
            
            var recentPrices = _priceHistory.TakeLast(window).ToArray();
            
            // Чистое изменение цены
            decimal netChange = Math.Abs(recentPrices[^1] - recentPrices[0]);
            
            // Общее движение (сумма всех изменений)
            decimal totalMovement = 0;
            for (int i = 1; i < recentPrices.Length; i++)
            {
                totalMovement += Math.Abs(recentPrices[i] - recentPrices[i-1]);
            }
            
            return totalMovement > _config.MinVolatility ? netChange / totalMovement : 0.5m;
        }

        /// <summary>
        /// Расчет отклонения от VWAP
        /// </summary>
        private decimal CalculateVWAPDeviation(TickData tick)
        {
            if (_vwapVolume == 0) return 0;
            
            decimal currentPrice = CalculateWeightedMidPrice(tick);
            decimal deviation = currentPrice - _currentVWAP;
            
            // Нормализация через текущую волатильность
            decimal currentVolatility = CalculateCurrentVolatility();
            
            return currentVolatility > _config.MinVolatility ? 
                deviation / currentVolatility : 0;
        }

        /// <summary>
        /// Расчет взвешенной средней цены с учетом размеров спреда
        /// </summary>
        private decimal CalculateWeightedMidPrice(TickData tick)
        {
            if (tick.BidSize + tick.AskSize == 0)
                return (tick.BestBid + tick.BestAsk) / 2;
                
            // Взвешиваем по противоположным объемам
            return (tick.BestBid * tick.AskSize + tick.BestAsk * tick.BidSize) / 
                   (tick.BidSize + tick.AskSize);
        }

        /// <summary>
        /// Определение режима рынка на основе Efficiency Ratio
        /// </summary>
        private MarketRegime DetermineMarketRegime()
        {
            if (_lastEfficiencyRatio < _config.ChoppyThreshold)
                return MarketRegime.Choppy;
            else if (_lastEfficiencyRatio > _config.TrendingThreshold)
                return MarketRegime.Trending;
            else
                return MarketRegime.Mixed;
        }

        /// <summary>
        /// Проверка условий для входа в позицию
        /// </summary>
        private async Task<OBIZSignal?> CheckEntryConditionsAsync(TickData tick, string symbol)
        {
            LogDebug($"🔍 {symbol} | ENTRY CHECK START");
            
            // Базовые проверки
            LogDebug($"📏 {symbol} | STEP 1 | ZScore check: {Math.Abs(_lastOBIZScore):F3} vs {_config.ZScoreThreshold}");
            if (Math.Abs(_lastOBIZScore) < _config.ZScoreThreshold)
            {
                LogDebug($"❌ {symbol} | STEP 1 FAILED | ZScore too low");
                return null;
            }
            LogDebug($"✅ {symbol} | STEP 1 PASSED | ZScore sufficient");
                
            LogDebug($"⚡ {symbol} | STEP 2 | Activity check");
            if (!IsHighActivityPeriod())
            {
                LogDebug($"❌ {symbol} | STEP 2 FAILED | Low activity period");
                return null;
            }
            LogDebug($"✅ {symbol} | STEP 2 PASSED | High activity period");
                
            LogDebug($"🏪 {symbol} | STEP 3 | Market conditions check");
            if (!IsGoodMarketConditions(tick))
            {
                LogDebug($"❌ {symbol} | STEP 3 FAILED | Poor market conditions");
                return null;
            }
            LogDebug($"✅ {symbol} | STEP 3 PASSED | Good market conditions");
            
            // Выбор стратегии в зависимости от режима рынка
            LogDebug($"🎯 {symbol} | STEP 4 | Market regime strategy: {_currentRegime}");
            var signal = _currentRegime switch
            {
                MarketRegime.Choppy => CheckMeanReversionEntry(tick, symbol),
                MarketRegime.Trending => CheckMomentumEntry(tick, symbol),
                MarketRegime.Mixed => CheckConservativeEntry(tick, symbol),
                _ => null
            };
            
            if (signal.HasValue)
            {
                LogDebug($"✅ {symbol} | STEP 4 PASSED | {_currentRegime} strategy generated signal");
            }
            else
            {
                LogDebug($"❌ {symbol} | STEP 4 FAILED | {_currentRegime} strategy no signal");
            }
            
            return signal;
        }

        /// <summary>
        /// Проверка периода высокой активности
        /// </summary>
        private bool IsHighActivityPeriod()
        {
            if (!_activityStats.HasSufficientData(_config.MinSamplesForPercentile))
            {
                LogDebug($"⚡ ACTIVITY | Insufficient data: {_activityStats.Count} < {_config.MinSamplesForPercentile} | ALLOWING");
                return true; // В начале торгуем все сигналы
            }
                
            decimal threshold = _activityStats.GetPercentile(_config.ActivityPercentileThreshold);
            bool result = _lastActivityScore >= threshold;
            LogDebug($"⚡ ACTIVITY | Result: {(result ? "PASS" : "FAIL")}", new Dictionary<string, object>
            {
                ["activityScore"] = _lastActivityScore,
                ["threshold"] = threshold,
                ["result"] = result,
                ["samplesCount"] = _activityStats.Count
            });
            return result;
        }

        /// <summary>
        /// Проверка качества рыночных условий
        /// </summary>
        private bool IsGoodMarketConditions(TickData tick)
        {
            // Проверка только объема в Order Book (спред фильтр убран как переоптимизация)
            long totalBookVolume = (tick.BidSize + tick.AskSize);
            
            LogDebug($"🏪 MARKET CONDITIONS", new Dictionary<string, object>
            {
                ["totalBookVolume"] = totalBookVolume,
                ["minRequiredVolume"] = _config.MinOrderBookVolume,
                ["volumeValid"] = totalBookVolume >= _config.MinOrderBookVolume
            });
            
            if (totalBookVolume < _config.MinOrderBookVolume)
            {
                return false;
            }
                
            return true;
        }

        /// <summary>
        /// Mean Reversion вход (для бокового рынка)
        /// </summary>
        private OBIZSignal? CheckMeanReversionEntry(TickData tick, string symbol)
        {
            // Проверяем противонаправленность VWAP deviation и OBI
            if (_lastVWAPDeviation * _lastOBIZScore >= 0)
                return null;
                
            // Требуем сильный дисбаланс + отклонение от VWAP
            if (Math.Abs(_lastVWAPDeviation) > _config.VWAPDeviationThreshold && 
                Math.Abs(_lastOBIZScore) > _config.StrongZScoreThreshold)
            {
                var direction = _lastOBIZScore > 0 ? TradeDirection.Sell : TradeDirection.Buy;
                return CreateSignal(direction, SignalConfidence.High, tick, symbol);
            }
            
            return null;
        }

        /// <summary>
        /// Momentum вход (для трендового рынка)
        /// </summary>
        private OBIZSignal? CheckMomentumEntry(TickData tick, string symbol)
        {
            // Для momentum требуем еще более сильный сигнал
            if (Math.Abs(_lastOBIZScore) < _config.StrongZScoreThreshold * 1.5m)
                return null;
                
            // Направление по дисбалансу (momentum)
            var direction = _lastOBIZScore < 0 ? TradeDirection.Buy : TradeDirection.Sell;
            return CreateSignal(direction, SignalConfidence.Medium, tick, symbol);
        }

        /// <summary>
        /// Консервативный вход (для смешанного режима)
        /// </summary>
        private OBIZSignal? CheckConservativeEntry(TickData tick, string symbol)
        {
            // Требуем выполнения всех условий
            if (Math.Abs(_lastOBIZScore) < _config.StrongZScoreThreshold ||
                Math.Abs(_lastVWAPDeviation) < _config.VWAPDeviationThreshold)
                return null;
                
            // Противонаправленность обязательна
            if (_lastVWAPDeviation * _lastOBIZScore >= 0)
                return null;
                
            var direction = _lastOBIZScore > 0 ? TradeDirection.Sell : TradeDirection.Buy;
            return CreateSignal(direction, SignalConfidence.Low, tick, symbol);
        }

        // Продолжение в следующем файле...
        
        private decimal GetTickSize()
        {
            // Заглушка - в реальной реализации получать из биржевой информации
            return 0.00001m;
        }

        private decimal CalculateStandardDeviation(IEnumerable<decimal> values, decimal mean)
        {
            var variance = values.Select(x => (x - mean) * (x - mean)).Average();
            return (decimal)Math.Sqrt((double)variance);
        }

        private decimal CalculateCurrentVolatility()
        {
            int window = Math.Min(50, _priceHistory.Count);
            if (window < 5) return _config.MinVolatility;
            
            var recentPrices = _priceHistory.TakeLast(window);
            return recentPrices.Zip(recentPrices.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        }

        // Остальные методы в следующем файле...
        
        private void LogInfo(string message, Dictionary<string, object>? data = null)
        {
            OBIZJsonLogger.Info("OBIZ_STRATEGY", message, data);
        }

        private void LogDebug(string message, Dictionary<string, object>? data = null)
        {
            if (_config.EnableDetailedLogging)
            {
                OBIZJsonLogger.Debug("OBIZ_STRATEGY", message, data);
            }
        }

        private void LogError(string message, Dictionary<string, object>? data = null)
        {
            OBIZJsonLogger.Error("OBIZ_STRATEGY", message, data);
        }

        /// <summary>
        /// Проверка готовности стратегии к работе
        /// </summary>
        public bool IsReady()
        {
            return _tickHistory.Count >= _config.MinHistoryForCalculation &&
                   _imbalanceStats.Count >= _config.MinSamplesForPercentile;
        }

        // Геттеры для статистик
        public OBIZStrategyStats GetCurrentStats()
        {
            return new OBIZStrategyStats
            {
                CurrentOBIZScore = _lastOBIZScore,
                CurrentActivityScore = _lastActivityScore,
                CurrentEfficiencyRatio = _lastEfficiencyRatio,
                CurrentVWAPDeviation = _lastVWAPDeviation,
                CurrentRegime = _currentRegime,
                TicksProcessed = _ticksProcessed,
                LastUpdate = _lastUpdate,
                HasSufficientData = _tickHistory.Count >= _config.MinHistoryForCalculation
            };
        }

        /// <summary>
        /// Создание торгового сигнала с адаптивными TP/SL
        /// </summary>
        private OBIZSignal CreateSignal(TradeDirection direction, SignalConfidence confidence, TickData tick, string symbol)
        {
            decimal currentPrice = CalculateWeightedMidPrice(tick);
            decimal currentVol = CalculateCurrentVolatility();
            decimal avgVol = GetAverageVolatility();
            
            // Адаптивные TP/SL на основе волатильности
            decimal volMultiplier = avgVol > 0 ? currentVol / avgVol : 1.0m;
            volMultiplier = Math.Max(volMultiplier, _config.MinVolatilityMultiplier);
            volMultiplier = Math.Min(volMultiplier, _config.MaxVolatilityMultiplier);
            
            // Корректировка по уровню уверенности
            decimal confidenceMultiplier = confidence switch
            {
                SignalConfidence.High => 1.2m,
                SignalConfidence.Medium => 1.0m,
                SignalConfidence.Low => 0.8m,
                _ => 1.0m
            };
            
            decimal tpRatio = _config.BaseTakeProfit * volMultiplier * confidenceMultiplier;
            decimal slRatio = _config.BaseStopLoss * volMultiplier;
            
            // Проверяем минимальный RR
            if (tpRatio / slRatio < _config.MinRiskReward)
            {
                tpRatio = slRatio * _config.MinRiskReward;
            }
            
            decimal tpPrice, slPrice;
            
            if (direction == TradeDirection.Buy)
            {
                tpPrice = currentPrice * (1 + tpRatio);
                slPrice = currentPrice * (1 - slRatio);
            }
            else
            {
                tpPrice = currentPrice * (1 - tpRatio);
                slPrice = currentPrice * (1 + slRatio);
            }
            
            var signal = new OBIZSignal
            {
                Direction = direction,
                Confidence = confidence,
                EntryPrice = currentPrice,
                TPPrice = tpPrice,
                SLPrice = slPrice,
                Timestamp = DateTime.UtcNow,
                OBIZScore = _lastOBIZScore,
                ActivityScore = _lastActivityScore,
                EfficiencyRatio = _lastEfficiencyRatio,
                VWAPDeviation = _lastVWAPDeviation,
                Regime = _currentRegime
            };
            
            if (_config.SaveSignalHistory)
            {
                _signalHistory.Add(signal);
                if (_signalHistory.Count > 1000)
                {
                    _signalHistory.RemoveRange(0, 500);
                }
            }
            
            return signal;
        }

        /// <summary>
        /// Управление открытой позицией
        /// </summary>
        private async Task<TradingDecision> ManageOpenPositionAsync(TickData tick, string symbol)
        {
            decimal currentPrice = CalculateWeightedMidPrice(tick);
            
            // Проверка Stop Loss
            if (_positionManager.ShouldStopLoss(currentPrice))
            {
                return new TradingDecision { Action = TradingAction.ClosePosition };
            }
            
            // Проверка Take Profit
            if (_positionManager.ShouldTakeProfit(currentPrice))
            {
                return new TradingDecision { Action = TradingAction.ClosePosition };
            }
            
            // Частичное закрытие
            decimal pnlRatio = _positionManager.GetPnLRatio(currentPrice);
            if (pnlRatio >= _config.PartialCloseRatio)
            {
                return new TradingDecision 
                { 
                    Action = TradingAction.PartialClose,
                    Percentage = 0.5m 
                };
            }
            
            // Time-based exit
            if (_positionManager.GetHoldingTimeSeconds() > _config.MaxHoldTimeSeconds)
            {
                return new TradingDecision { Action = TradingAction.ClosePosition };
            }
            
            return TradingDecision.NoAction;
        }

        private decimal GetAverageVolatility()
        {
            int window = Math.Min(500, _priceHistory.Count);
            if (window < 10) return _config.MinVolatility;
            
            decimal totalVol = 0;
            var prices = _priceHistory.TakeLast(window).ToArray();
            
            for (int i = 1; i < prices.Length; i++)
            {
                totalVol += Math.Abs(prices[i] - prices[i-1]);
            }
            
            return totalVol / (prices.Length - 1);
        }
    }
}
