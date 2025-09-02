using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using Models;
using Config;

namespace Services
{
    /// <summary>
    /// Состояние сигналов (оставлено для совместимости, но больше не используется в линейной логике)
    /// </summary>
    public class SignalState
    {
        public bool PreviousLongCondition { get; set; } = false;
        public bool PreviousShortCondition { get; set; } = false;
        public DateTime LastUpdate { get; set; } = DateTime.MinValue;
    }

    public class TradingStrategyService
    {
        private readonly BackendConfig _config;
        private readonly ConcurrentDictionary<string, SignalState> _signalStates = new(); // Не используется в линейной логике
        private readonly FifteenSecondCandleService? _fifteenSecondService;

        public TradingStrategyService(BackendConfig config, FifteenSecondCandleService? fifteenSecondService = null)
        {
            _config = config;
            _fifteenSecondService = fifteenSecondService;
        }

        /// <summary>
        /// Анализ торговых сигналов для монеты - только 15-секундная торговля
        /// </summary>
        public StrategyResult AnalyzeCoin(CoinData coinData)
        {
            // Только 15-секундная торговля для сигналов
            if (_config.EnableFifteenSecondTrading && _fifteenSecondService != null)
            {
                return AnalyzeCoinFifteenSecond(coinData);
            }
            
            // Если 15s торговля отключена - возвращаем FLAT (торговля отключена)
            return new StrategyResult
            {
                Symbol = coinData?.Symbol ?? "UNKNOWN",
                FinalSignal = "FLAT",
                Reason = "15-секундная торговля отключена - торговые сигналы недоступны"
            };
        }

        /// <summary>
        /// Анализ на 15-секундных свечах
        /// </summary>
        private StrategyResult AnalyzeCoinFifteenSecond(CoinData coinData)
        {
            if (_fifteenSecondService == null)
            {
                return new StrategyResult
                {
                    Symbol = coinData?.Symbol ?? "UNKNOWN",
                    FinalSignal = "FLAT",
                    Reason = "15s сервис не инициализирован"
                };
            }

            // Проверяем готовность символа (прогрев)
            if (!_fifteenSecondService.IsSymbolReady(coinData.Symbol))
            {
                return new StrategyResult
                {
                    Symbol = coinData.Symbol,
                    FinalSignal = "FLAT",
                    Reason = $"Прогрев 15s свечей: {_fifteenSecondService.GetFifteenSecondCandles(coinData.Symbol)?.Count ?? 0}/{_config.FifteenSecondWarmupCandles}"
                };
            }

            // Получаем 15-секундные свечи
            var fifteenSecondCandles = _fifteenSecondService.GetFifteenSecondCandles(coinData.Symbol);
            if (fifteenSecondCandles == null || fifteenSecondCandles.Count < Math.Max(_config.ZScoreSmaPeriod, _config.StrategySmaPeriod))
            {
                return new StrategyResult
                {
                    Symbol = coinData.Symbol,
                    FinalSignal = "FLAT",
                    Reason = "Недостаточно 15s данных для анализа"
                };
            }

            var result = new StrategyResult
            {
                Symbol = coinData.Symbol,
                CurrentPrice = coinData.CurrentPrice,
                Natr = coinData.Natr ?? 0,
                Timestamp = DateTime.UtcNow
            };

            // Стратегия 1: Z-Score на 15s свечах
            var (zScore, zScoreSignal) = TechnicalAnalysisService.CalculateZScoreSma(
                fifteenSecondCandles, 
                _config.ZScoreSmaPeriod, 
                _config.ZScoreThreshold);

            result.ZScore = zScore;
            result.ZScoreSignal = zScoreSignal;

            // Стратегия 2: SMA Trend на 15s свечах  
            var (smaScore, smaSignal) = TechnicalAnalysisService.CalculateSmaStrategy(
                fifteenSecondCandles, 
                _config.StrategySmaPeriod);

            result.Sma = smaScore;
            result.SmaSignal = smaSignal;

            // Линейная комбинация сигналов - генерирует сигнал на каждой подходящей свече
            result.FinalSignal = CombineSignalsLinear(coinData.Symbol, result.ZScoreSignal, result.SmaSignal);
            result.Reason = GetSignalReason(result.ZScoreSignal, result.SmaSignal, result.ZScore, coinData.CurrentPrice, result.Sma) + " (15s)";

            return result;
        }



        /// <summary>
        /// Линейная комбинация сигналов без edge-detection - генерирует сигнал на каждой подходящей свече
        /// </summary>
        private string CombineSignalsLinear(string symbol, string zScoreSignal, string smaSignal)
        {
            // Простая линейная логика: если оба условия выполнены - генерируем сигнал
            bool currentLongCondition = smaSignal == "LONG" && zScoreSignal == "LONG";   // (close > SMA) & (Z <= -threshold)
            bool currentShortCondition = smaSignal == "SHORT" && zScoreSignal == "SHORT"; // (close < SMA) & (Z >= +threshold)

            // Исключаем одновременные сигналы
            if (currentLongCondition && currentShortCondition)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ КОНФЛИКТ: {symbol} Одновременные LONG и SHORT - игнорируем");
                return "FLAT";
            }

            // Возвращаем сигнал на каждой подходящей свече
            if (currentLongCondition)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 LINEAR LONG СИГНАЛ: {symbol}");
                return "LONG";
            }
            
            if (currentShortCondition)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔥 LINEAR SHORT СИГНАЛ: {symbol}");
                return "SHORT";
            }

            // Условия не выполнены
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
