using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Services.OBIZScore.Core;
using Services.OBIZScore.Config;
using Models;
using Config;

namespace Services.OBIZScore
{
    /// <summary>
    /// Интегрированный сервис стратегий - объединяет OBIZ-Score с существующими стратегиями
    /// </summary>
    public class IntegratedStrategyService
    {
        private readonly TradingStrategyService _legacyStrategy;
        private readonly ConcurrentDictionary<string, OBIZScoreStrategy> _obizStrategies;
        private readonly TickDataAdapter _tickAdapter;
        private readonly OBIZStrategyConfig _obizConfig;
        private readonly StrategyConfig _strategyConfig;

        public IntegratedStrategyService(
            TradingStrategyService legacyStrategy,
            OBIZStrategyConfig obizConfig,
            StrategyConfig strategyConfig)
        {
            _legacyStrategy = legacyStrategy ?? throw new ArgumentNullException(nameof(legacyStrategy));
            _obizConfig = obizConfig ?? throw new ArgumentNullException(nameof(obizConfig));
            _strategyConfig = strategyConfig ?? throw new ArgumentNullException(nameof(strategyConfig));
            
            _obizStrategies = new ConcurrentDictionary<string, OBIZScoreStrategy>();
            _tickAdapter = new TickDataAdapter();
        }

        /// <summary>
        /// Анализ монеты с использованием выбранных стратегий
        /// </summary>
        public async Task<IntegratedStrategyResult> AnalyzeCoinAsync(CoinData coinData)
        {
            var result = new IntegratedStrategyResult
            {
                Symbol = coinData?.Symbol ?? "UNKNOWN",
                Timestamp = DateTime.UtcNow,
                CurrentPrice = coinData?.CurrentPrice ?? 0
            };

            try
            {
                switch (_strategyConfig.Mode)
                {
                    case StrategyMode.Legacy:
                        result = await AnalyzeLegacyOnlyAsync(coinData);
                        break;
                        
                    case StrategyMode.OBIZOnly:
                        result = await AnalyzeOBIZOnlyAsync(coinData);
                        break;
                        
                    case StrategyMode.Combined:
                        result = await AnalyzeCombinedAsync(coinData);
                        break;
                        
                    default:
                        result.FinalSignal = "FLAT";
                        result.Reason = "Unknown strategy mode";
                        break;
                }
            }
            catch (Exception ex)
            {
                result.FinalSignal = "FLAT";
                result.Reason = $"Error in analysis: {ex.Message}";
                LogError($"Error analyzing {coinData?.Symbol}: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Анализ только с использованием существующих стратегий
        /// </summary>
        private async Task<IntegratedStrategyResult> AnalyzeLegacyOnlyAsync(CoinData coinData)
        {
            var legacyResult = _legacyStrategy.AnalyzeCoin(coinData);
            
            return new IntegratedStrategyResult
            {
                Symbol = coinData.Symbol,
                CurrentPrice = coinData.CurrentPrice,
                Timestamp = DateTime.UtcNow,
                
                // Legacy данные
                ZScore = legacyResult.ZScore,
                ZScoreSignal = legacyResult.ZScoreSignal,
                Sma = legacyResult.Sma,
                SmaSignal = legacyResult.SmaSignal,
                
                // Финальный результат
                FinalSignal = legacyResult.FinalSignal,
                Reason = legacyResult.Reason + " (Legacy)",
                
                // OBIZ данные (пустые)
                OBIZScore = 0,
                ActivityScore = 0,
                EfficiencyRatio = 0.5m,
                VWAPDeviation = 0,
                MarketRegime = MarketRegime.Mixed,
                SignalConfidence = SignalConfidence.Low
            };
        }

        /// <summary>
        /// Анализ только с использованием OBIZ-Score стратегии
        /// </summary>
        private async Task<IntegratedStrategyResult> AnalyzeOBIZOnlyAsync(CoinData coinData)
        {
            var result = new IntegratedStrategyResult
            {
                Symbol = coinData.Symbol,
                CurrentPrice = coinData.CurrentPrice,
                Timestamp = DateTime.UtcNow
            };

            // Получаем или создаем OBIZ стратегию для символа
            var obizStrategy = GetOrCreateOBIZStrategy(coinData.Symbol);
            
            if (!obizStrategy.IsReady())
            {
                // Подготавливаем стратегию историческими данными
                await WarmupOBIZStrategyAsync(obizStrategy, coinData);
                
                if (!obizStrategy.IsReady())
                {
                    result.FinalSignal = "FLAT";
                    result.Reason = "OBIZ strategy warming up";
                    return result;
                }
            }

            // Создаем симулированный тик из текущих данных
            var tick = _tickAdapter.CreateRealTimeTick(coinData.Symbol, coinData.CurrentPrice, coinData.Volume24h);
            
            // Анализируем с помощью OBIZ стратегии
            var decision = await obizStrategy.ProcessTickAsync(tick, coinData.Symbol);
            var stats = obizStrategy.GetCurrentStats();

            // Заполняем результат
            result.OBIZScore = stats.CurrentOBIZScore;
            result.ActivityScore = stats.CurrentActivityScore;
            result.EfficiencyRatio = stats.CurrentEfficiencyRatio;
            result.VWAPDeviation = stats.CurrentVWAPDeviation;
            result.MarketRegime = stats.CurrentRegime;

            // Определяем финальный сигнал
            if (decision.Signal.HasValue)
            {
                var signal = decision.Signal.Value;
                result.FinalSignal = signal.Direction == TradeDirection.Buy ? "LONG" : "SHORT";
                result.SignalConfidence = signal.Confidence;
                result.Reason = $"OBIZ {signal.Confidence} signal: Score={stats.CurrentOBIZScore:F2}, Regime={stats.CurrentRegime}";
                
                // Сохраняем данные сигнала для возможного исполнения
                result.EntryPrice = signal.EntryPrice;
                result.TPPrice = signal.TPPrice;
                result.SLPrice = signal.SLPrice;
            }
            else
            {
                result.FinalSignal = "FLAT";
                result.SignalConfidence = SignalConfidence.Low;
                result.Reason = $"No OBIZ signal: Score={stats.CurrentOBIZScore:F2}, Activity below threshold";
            }

            return result;
        }

        /// <summary>
        /// Комбинированный анализ (Legacy + OBIZ)
        /// </summary>
        private async Task<IntegratedStrategyResult> AnalyzeCombinedAsync(CoinData coinData)
        {
            // Получаем результаты от обеих стратегий
            var legacyTask = Task.Run(() => _legacyStrategy.AnalyzeCoin(coinData));
            var obizTask = AnalyzeOBIZOnlyAsync(coinData);

            await Task.WhenAll(legacyTask, obizTask);

            var legacyResult = legacyTask.Result;
            var obizResult = obizTask.Result;

            var result = new IntegratedStrategyResult
            {
                Symbol = coinData.Symbol,
                CurrentPrice = coinData.CurrentPrice,
                Timestamp = DateTime.UtcNow,
                
                // Legacy данные
                ZScore = legacyResult.ZScore,
                ZScoreSignal = legacyResult.ZScoreSignal,
                Sma = legacyResult.Sma,
                SmaSignal = legacyResult.SmaSignal,
                
                // OBIZ данные
                OBIZScore = obizResult.OBIZScore,
                ActivityScore = obizResult.ActivityScore,
                EfficiencyRatio = obizResult.EfficiencyRatio,
                VWAPDeviation = obizResult.VWAPDeviation,
                MarketRegime = obizResult.MarketRegime,
                SignalConfidence = obizResult.SignalConfidence,
                EntryPrice = obizResult.EntryPrice,
                TPPrice = obizResult.TPPrice,
                SLPrice = obizResult.SLPrice
            };

            // Комбинируем сигналы
            result.FinalSignal = CombineSignals(legacyResult.FinalSignal, obizResult.FinalSignal);
            result.Reason = GetCombinedReason(legacyResult, obizResult);

            return result;
        }

        /// <summary>
        /// Логика комбинирования сигналов от разных стратегий
        /// </summary>
        private string CombineSignals(string legacySignal, string obizSignal)
        {
            // Если нет OBIZ сигнала, используем Legacy
            if (obizSignal == "FLAT")
                return legacySignal;
                
            // Если нет Legacy сигнала, используем OBIZ
            if (legacySignal == "FLAT")
                return obizSignal;
                
            // Если оба сигнала в одном направлении - усиливаем
            if (legacySignal == obizSignal)
                return legacySignal;
                
            // Если сигналы противоречат друг другу
            if ((legacySignal == "LONG" && obizSignal == "SHORT") || 
                (legacySignal == "SHORT" && obizSignal == "LONG"))
            {
                // Приоритет OBIZ сигналам с высокой уверенностью
                // Иначе конфликт = FLAT
                return "FLAT";
            }

            return "FLAT";
        }

        /// <summary>
        /// Формирование объяснения комбинированного сигнала
        /// </summary>
        private string GetCombinedReason(StrategyResult legacy, IntegratedStrategyResult obiz)
        {
            if (legacy.FinalSignal == obiz.FinalSignal && legacy.FinalSignal != "FLAT")
            {
                return $"Both strategies: Legacy={legacy.FinalSignal}, OBIZ={obiz.FinalSignal} ({obiz.SignalConfidence})";
            }
            else if (legacy.FinalSignal != "FLAT" && obiz.FinalSignal == "FLAT")
            {
                return $"Legacy only: {legacy.Reason}";
            }
            else if (legacy.FinalSignal == "FLAT" && obiz.FinalSignal != "FLAT")
            {
                return $"OBIZ only: {obiz.Reason}";
            }
            else if (legacy.FinalSignal != obiz.FinalSignal)
            {
                return $"Strategy conflict: Legacy={legacy.FinalSignal} vs OBIZ={obiz.FinalSignal}";
            }
            else
            {
                return "No signals from any strategy";
            }
        }

        /// <summary>
        /// Получение или создание OBIZ стратегии для символа
        /// </summary>
        private OBIZScoreStrategy GetOrCreateOBIZStrategy(string symbol)
        {
            return _obizStrategies.GetOrAdd(symbol, _ => new OBIZScoreStrategy(_obizConfig));
        }

        /// <summary>
        /// Прогрев OBIZ стратегии историческими данными
        /// </summary>
        private async Task WarmupOBIZStrategyAsync(OBIZScoreStrategy strategy, CoinData coinData)
        {
            if (coinData.RecentCandles == null || coinData.RecentCandles.Count == 0)
                return;

            // Конвертируем исторические свечи в тики для прогрева
            foreach (var candle in coinData.RecentCandles.TakeLast(20)) // Берем последние 20 свечей
            {
                var ticks = _tickAdapter.ConvertCandleToTicks(candle, coinData.Symbol, 5); // 5 тиков на свечу
                
                foreach (var tick in ticks)
                {
                    await strategy.ProcessTickAsync(tick, coinData.Symbol);
                }
            }
        }

        /// <summary>
        /// Массовый анализ всех монет
        /// </summary>
        public async Task<List<IntegratedStrategyResult>> AnalyzeAllCoinsAsync(List<CoinData> coins)
        {
            var tasks = coins.Select(coin => AnalyzeCoinAsync(coin));
            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        /// <summary>
        /// Получение активных сигналов
        /// </summary>
        public async Task<List<IntegratedStrategyResult>> GetActiveSignalsAsync(List<CoinData> coins)
        {
            var allResults = await AnalyzeAllCoinsAsync(coins);
            
            return allResults
                .Where(r => r.FinalSignal == "LONG" || r.FinalSignal == "SHORT")
                .OrderByDescending(r => GetSignalStrength(r))
                .ToList();
        }

        /// <summary>
        /// Расчет силы сигнала для сортировки
        /// </summary>
        private decimal GetSignalStrength(IntegratedStrategyResult result)
        {
            decimal strength = 0;
            
            // Добавляем силу от Legacy стратегий
            strength += Math.Abs(result.ZScore) * 0.3m;
            
            // Добавляем силу от OBIZ стратегии
            strength += Math.Abs(result.OBIZScore) * 0.7m;
            
            // Бонус за уверенность OBIZ
            strength += result.SignalConfidence switch
            {
                SignalConfidence.High => 1.0m,
                SignalConfidence.Medium => 0.5m,
                SignalConfidence.Low => 0.2m,
                _ => 0
            };

            return strength;
        }

        /// <summary>
        /// Очистка ресурсов для символа
        /// </summary>
        public void ClearSymbol(string symbol)
        {
            _obizStrategies.TryRemove(symbol, out _);
            _tickAdapter.ClearHistory(symbol);
        }

        /// <summary>
        /// Статистика по стратегиям
        /// </summary>
        public StrategyStatisticsExtended GetStatistics(List<IntegratedStrategyResult> results)
        {
            if (!results.Any())
                return new StrategyStatisticsExtended();

            return new StrategyStatisticsExtended
            {
                TotalCoins = results.Count,
                LongSignals = results.Count(r => r.FinalSignal == "LONG"),
                ShortSignals = results.Count(r => r.FinalSignal == "SHORT"),
                FlatSignals = results.Count(r => r.FinalSignal == "FLAT"),
                
                // Legacy статистики
                AvgZScore = Math.Round(results.Average(r => Math.Abs(r.ZScore)), 2),
                MaxZScore = Math.Round(results.Max(r => Math.Abs(r.ZScore)), 2),
                
                // OBIZ статистики
                AvgOBIZScore = Math.Round(results.Average(r => Math.Abs(r.OBIZScore)), 2),
                MaxOBIZScore = Math.Round(results.Max(r => Math.Abs(r.OBIZScore)), 2),
                HighConfidenceSignals = results.Count(r => r.SignalConfidence == SignalConfidence.High),
                
                // Режимы рынка
                ChoppyMarkets = results.Count(r => r.MarketRegime == MarketRegime.Choppy),
                TrendingMarkets = results.Count(r => r.MarketRegime == MarketRegime.Trending),
                MixedMarkets = results.Count(r => r.MarketRegime == MarketRegime.Mixed)
            };
        }

        private void LogError(string message)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] INTEGRATED ERROR: {message}");
        }
    }

    /// <summary>
    /// Расширенный результат интегрированной стратегии
    /// </summary>
    public class IntegratedStrategyResult
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal CurrentPrice { get; set; }
        public DateTime Timestamp { get; set; }

        // Legacy стратегии
        public decimal ZScore { get; set; }
        public string ZScoreSignal { get; set; } = "FLAT";
        public decimal Sma { get; set; }
        public string SmaSignal { get; set; } = "FLAT";

        // OBIZ стратегия
        public decimal OBIZScore { get; set; }
        public decimal ActivityScore { get; set; }
        public decimal EfficiencyRatio { get; set; }
        public decimal VWAPDeviation { get; set; }
        public MarketRegime MarketRegime { get; set; }
        public SignalConfidence SignalConfidence { get; set; }

        // Данные сигнала для торговли
        public decimal EntryPrice { get; set; }
        public decimal TPPrice { get; set; }
        public decimal SLPrice { get; set; }

        // Финальный результат
        public string FinalSignal { get; set; } = "FLAT";
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Расширенная статистика стратегий
    /// </summary>
    public class StrategyStatisticsExtended
    {
        public int TotalCoins { get; set; }
        public int LongSignals { get; set; }
        public int ShortSignals { get; set; }
        public int FlatSignals { get; set; }
        
        // Legacy статистики
        public decimal AvgZScore { get; set; }
        public decimal MaxZScore { get; set; }
        
        // OBIZ статистики
        public decimal AvgOBIZScore { get; set; }
        public decimal MaxOBIZScore { get; set; }
        public int HighConfidenceSignals { get; set; }
        
        // Распределение по режимам рынка
        public int ChoppyMarkets { get; set; }
        public int TrendingMarkets { get; set; }
        public int MixedMarkets { get; set; }
        
        public decimal SignalRate => TotalCoins > 0 ? (decimal)(LongSignals + ShortSignals) / TotalCoins * 100 : 0;
    }
}
