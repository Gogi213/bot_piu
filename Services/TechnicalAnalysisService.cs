using System;
using System.Collections.Generic;
using System.Linq;
using Models;

namespace Services
{
    public class TechnicalAnalysisService
    {
        /// <summary>
        /// Расчет Normalized Average True Range (NATR)
        /// NATR = ATR / Close * 100
        /// </summary>
        public static decimal? CalculateNatr(List<CandleData> candles, int periods = 30)
        {
            if (candles == null || candles.Count < periods + 1)
                return null;

            var sortedCandles = candles.OrderBy(c => c.OpenTime).ToList();
            var trueRanges = new List<decimal>();

            // Вычисляем True Range для каждой свечи
            for (int i = 1; i < sortedCandles.Count; i++)
            {
                var current = sortedCandles[i];
                var previous = sortedCandles[i - 1];

                var tr1 = current.High - current.Low;
                var tr2 = Math.Abs(current.High - previous.Close);
                var tr3 = Math.Abs(current.Low - previous.Close);

                var trueRange = Math.Max(tr1, Math.Max(tr2, tr3));
                trueRanges.Add(trueRange);
            }

            // Берем последние periods значений для расчета ATR
            var recentTrueRanges = trueRanges.TakeLast(periods).ToList();
            if (recentTrueRanges.Count < periods)
                return null;

            var atr = recentTrueRanges.Average();
            var lastClose = sortedCandles.Last().Close;
            
            if (lastClose == 0)
                return null;

            var natr = (atr / lastClose) * 100;
            return Math.Round(natr, 4);
        }

        /// <summary>
        /// Расчет Z-Score для SMA стратегии
        /// Z-Score = (Current Price - SMA) / Standard Deviation
        /// </summary>
        public static (decimal zScore, string signal) CalculateZScoreSma(List<CandleData> candles, int smaPeriod = 17, decimal threshold = 1.4m)
        {
            if (candles == null || candles.Count < smaPeriod)
                return (0, "FLAT");

            var sortedCandles = candles.OrderBy(c => c.OpenTime).ToList();
            var recentPrices = sortedCandles.TakeLast(smaPeriod).Select(c => c.Close).ToList();

            var currentPrice = sortedCandles.Last().Close;
            var sma = recentPrices.Average();
            
            // Стандартное отклонение
            var variance = recentPrices.Select(price => Math.Pow((double)(price - sma), 2)).Average();
            var stdDev = (decimal)Math.Sqrt(variance);

            if (stdDev == 0)
                return (0, "FLAT");

            var zScore = (currentPrice - sma) / stdDev;
            zScore = Math.Round(zScore, 4);

            // Определяем сигнал (инвертированная логика для mean reversion)
            string signal = "FLAT";
            if (zScore > threshold)
                signal = "SHORT"; // Цена слишком высоко - шорт (ожидаем падение)
            else if (zScore < -threshold)
                signal = "LONG";  // Цена слишком низко - лонг (ожидаем рост)

            return (zScore, signal);
        }

        /// <summary>
        /// Стратегия на основе Simple Moving Average
        /// </summary>
        public static (decimal sma, string signal) CalculateSmaStrategy(List<CandleData> candles, int smaPeriod = 30)
        {
            if (candles == null || candles.Count < smaPeriod)
            {
                return (0, "FLAT");
            }

            var sortedCandles = candles.OrderBy(c => c.OpenTime).ToList();
            var recentPrices = sortedCandles.TakeLast(smaPeriod).Select(c => c.Close).ToList();
            var currentPrice = sortedCandles.Last().Close;
            
            var sma = recentPrices.Average();
            sma = Math.Round(sma, 8);

            // Определяем сигнал
            string signal = "FLAT";
            if (currentPrice > sma)
            {
                signal = "LONG";  // Цена выше SMA - лонг
            }
            else if (currentPrice < sma)
            {
                signal = "SHORT"; // Цена ниже SMA - шорт
            }

            return (sma, signal);
        }

        /// <summary>
        /// Простая скользящая средняя
        /// </summary>
        public static decimal CalculateSma(List<decimal> values, int period)
        {
            if (values == null || values.Count < period)
                return 0;

            return values.TakeLast(period).Average();
        }

        /// <summary>
        /// Добавление новой свечи к существующим данным с ограничением размера
        /// </summary>
        public static void UpdateCandleData(CoinData coinData, CandleData newCandle, int maxCandles = 50)
        {
            coinData.RecentCandles.Add(newCandle);
            
            // Сортируем по времени и оставляем только последние maxCandles свечей
            coinData.RecentCandles = coinData.RecentCandles
                .OrderBy(c => c.OpenTime)
                .TakeLast(maxCandles)
                .ToList();

            coinData.CurrentPrice = newCandle.Close;
            coinData.LastUpdated = DateTime.UtcNow;

            // Пересчитываем NATR если достаточно данных
            coinData.Natr = CalculateNatr(coinData.RecentCandles);
        }
    }
}
