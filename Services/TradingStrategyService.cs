using System;
using System.Collections.Generic;
using System.Linq;
using Models;
using Config;

namespace Services
{
    public class TradingStrategyService
    {
        private readonly BackendConfig _config;

        public TradingStrategyService(BackendConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Анализ торговых сигналов для монеты с использованием двойной стратегии
        /// </summary>
        public StrategyResult AnalyzeCoin(CoinData coinData)
        {
            if (coinData?.RecentCandles == null || coinData.RecentCandles.Count < Math.Max(_config.ZScoreSmaPeriod, _config.StrategySmaPeriod))
            {
                return new StrategyResult
                {
                    Symbol = coinData?.Symbol ?? "UNKNOWN",
                    FinalSignal = "FLAT",
                    Reason = "Недостаточно данных для анализа"
                };
            }

            var result = new StrategyResult
            {
                Symbol = coinData.Symbol,
                CurrentPrice = coinData.CurrentPrice,
                Natr = coinData.Natr ?? 0,
                Timestamp = DateTime.UtcNow
            };

            // Стратегия 1: Z-Score (Mean Reversion)
            var (zScore, zScoreSignal) = TechnicalAnalysisService.CalculateZScoreSma(
                coinData.RecentCandles, 
                _config.ZScoreSmaPeriod, 
                _config.ZScoreThreshold);

            result.ZScore = zScore;
            result.ZScoreSignal = zScoreSignal;

            // Стратегия 2: SMA Trend Following
            var (sma, smaSignal) = TechnicalAnalysisService.CalculateSmaStrategy(
                coinData.RecentCandles, 
                _config.StrategySmaPeriod);

            result.Sma = sma;
            result.SmaSignal = smaSignal;

            // Комбинированная логика: обе стратегии должны давать одинаковый сигнал
            result.FinalSignal = CombineSignals(zScoreSignal, smaSignal);
            result.Reason = GetSignalReason(zScoreSignal, smaSignal, zScore, coinData.CurrentPrice, sma);

            return result;
        }

        /// <summary>
        /// Комбинирование сигналов от двух стратегий
        /// </summary>
        private string CombineSignals(string zScoreSignal, string smaSignal)
        {
            // Обе стратегии должны давать одинаковый сигнал для входа в позицию
            if (zScoreSignal == smaSignal && (zScoreSignal == "LONG" || zScoreSignal == "SHORT"))
            {
                return zScoreSignal;
            }

            // В остальных случаях - нет сигнала
            return "FLAT";
        }

        /// <summary>
        /// Объяснение причины сигнала
        /// </summary>
        private string GetSignalReason(string zScoreSignal, string smaSignal, decimal zScore, decimal currentPrice, decimal sma)
        {
            if (zScoreSignal == smaSignal && zScoreSignal != "FLAT")
            {
                var priceVsSma = currentPrice > sma ? "выше" : "ниже";
                var zScoreDirection = zScore > 0 ? "переоценена" : "недооценена";
                
                return $"Обе стратегии: цена {priceVsSma} SMA({_config.StrategySmaPeriod}) и {zScoreDirection} (Z={zScore:F2})";
            }
            else if (zScoreSignal != smaSignal)
            {
                return $"Конфликт стратегий: Z-Score={zScoreSignal} vs SMA={smaSignal}";
            }
            else
            {
                return $"Нет сильного сигнала: Z={zScore:F2}, цена близко к SMA";
            }
        }

        /// <summary>
        /// Массовый анализ всех монет в пуле
        /// </summary>
        public List<StrategyResult> AnalyzeAllCoins(List<CoinData> coins)
        {
            var results = new List<StrategyResult>();

            foreach (var coin in coins)
            {
                try
                {
                    var result = AnalyzeCoin(coin);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка анализа {coin.Symbol}: {ex.Message}");
                    
                    results.Add(new StrategyResult
                    {
                        Symbol = coin.Symbol,
                        FinalSignal = "FLAT",
                        Reason = $"Ошибка анализа: {ex.Message}"
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// Получение только активных торговых сигналов
        /// </summary>
        public List<StrategyResult> GetActiveSignals(List<CoinData> coins)
        {
            var allResults = AnalyzeAllCoins(coins);
            
            return allResults
                .Where(r => r.FinalSignal == "LONG" || r.FinalSignal == "SHORT")
                .OrderByDescending(r => Math.Abs(r.ZScore)) // Сортируем по силе сигнала
                .ToList();
        }

        /// <summary>
        /// Статистика по сигналам
        /// </summary>
        public StrategyStatistics GetStatistics(List<StrategyResult> results)
        {
            if (!results.Any())
                return new StrategyStatistics();

            var stats = new StrategyStatistics
            {
                TotalCoins = results.Count,
                LongSignals = results.Count(r => r.FinalSignal == "LONG"),
                ShortSignals = results.Count(r => r.FinalSignal == "SHORT"),
                FlatSignals = results.Count(r => r.FinalSignal == "FLAT"),
                AvgZScore = Math.Round(results.Average(r => Math.Abs(r.ZScore)), 2),
                MaxZScore = Math.Round(results.Max(r => Math.Abs(r.ZScore)), 2),
                AvgNatr = Math.Round(results.Where(r => r.Natr > 0).Average(r => r.Natr), 2)
            };

            stats.SignalRate = Math.Round((decimal)(stats.LongSignals + stats.ShortSignals) / stats.TotalCoins * 100, 1);

            return stats;
        }
    }

    public class StrategyResult
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal CurrentPrice { get; set; }
        public decimal Natr { get; set; }
        public DateTime Timestamp { get; set; }

        // Z-Score стратегия
        public decimal ZScore { get; set; }
        public string ZScoreSignal { get; set; } = "FLAT";

        // SMA стратегия  
        public decimal Sma { get; set; }
        public string SmaSignal { get; set; } = "FLAT";

        // Финальный результат
        public string FinalSignal { get; set; } = "FLAT";
        public string Reason { get; set; } = string.Empty;
    }

    public class StrategyStatistics
    {
        public int TotalCoins { get; set; }
        public int LongSignals { get; set; }
        public int ShortSignals { get; set; }
        public int FlatSignals { get; set; }
        public decimal SignalRate { get; set; } // Процент монет с сигналами
        public decimal AvgZScore { get; set; }
        public decimal MaxZScore { get; set; }
        public decimal AvgNatr { get; set; }
    }
}
