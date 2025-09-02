using System;
using System.Collections.Generic;
using System.Linq;
using Services.OBIZScore.Core;
using Models;

namespace Services.OBIZScore
{
    /// <summary>
    /// Адаптер для конвертации свечных данных в симулированные тиковые данные
    /// Временное решение до получения доступа к реальным тикам и Order Book
    /// </summary>
    public class TickDataAdapter
    {
        private readonly Random _random;
        private readonly Dictionary<string, decimal> _lastPrices;
        private readonly Dictionary<string, long> _lastVolumes;

        public TickDataAdapter()
        {
            _random = new Random();
            _lastPrices = new Dictionary<string, decimal>();
            _lastVolumes = new Dictionary<string, long>();
        }

        /// <summary>
        /// Конвертирует свечу в набор симулированных тиков
        /// </summary>
        public List<TickData> ConvertCandleToTicks(CandleData candle, string symbol, int tickCount = 10)
        {
            var ticks = new List<TickData>();
            
            if (candle == null) return ticks;

            // Генерируем временные метки внутри свечи
            var candleDuration = TimeSpan.FromSeconds(15); // 15-секундные свечи
            var tickInterval = candleDuration.TotalMilliseconds / tickCount;

            // Создаем ценовой путь от Open к Close через High и Low
            var pricePoints = GeneratePricePath(candle.Open, candle.High, candle.Low, candle.Close, tickCount);
            var volumeDistribution = DistributeVolume((long)candle.Volume, tickCount);

            for (int i = 0; i < tickCount; i++)
            {
                var tickTime = candle.OpenTime.AddMilliseconds(i * tickInterval);
                var price = pricePoints[i];
                var volume = volumeDistribution[i];

                var tick = CreateSimulatedTick(symbol, tickTime, price, volume);
                ticks.Add(tick);
            }

            // Обновляем последние значения
            _lastPrices[symbol] = candle.Close;
            _lastVolumes[symbol] = (long)candle.Volume;

            return ticks;
        }

        /// <summary>
        /// Создает симулированный тик с Order Book данными
        /// </summary>
        private TickData CreateSimulatedTick(string symbol, DateTime timestamp, decimal price, long volume)
        {
            // Симулируем спред (0.01% от цены)
            decimal spread = price * 0.0001m;
            decimal halfSpread = spread / 2;

            decimal bestBid = price - halfSpread;
            decimal bestAsk = price + halfSpread;

            // Симулируем размеры на лучших уровнях
            long bidSize = (long)(volume * (0.3m + (decimal)_random.NextDouble() * 0.4m)); // 30-70% от объема
            long askSize = volume - bidSize;

            // Создаем симулированный Order Book (10 уровней)
            var bids = GenerateOrderBookSide(bestBid, bidSize, 10, false);
            var asks = GenerateOrderBookSide(bestAsk, askSize, 10, true);

            // Определяем направление сделки на основе движения цены
            var direction = DetermineTradeDirection(symbol, price);

            return new TickData
            {
                Timestamp = timestamp,
                Price = price,
                Volume = volume,
                BestBid = bestBid,
                BestAsk = bestAsk,
                BidSize = bidSize,
                AskSize = askSize,
                Bids = bids,
                Asks = asks,
                Direction = direction
            };
        }

        /// <summary>
        /// Генерирует ценовой путь от открытия к закрытию через экстремумы
        /// </summary>
        private decimal[] GeneratePricePath(decimal open, decimal high, decimal low, decimal close, int points)
        {
            var path = new decimal[points];
            
            if (points <= 1)
            {
                path[0] = close;
                return path;
            }

            // Определяем ключевые точки
            int highPoint = _random.Next(1, points - 1);
            int lowPoint = _random.Next(1, points - 1);
            
            // Убеждаемся что high и low в разных точках
            if (highPoint == lowPoint)
            {
                lowPoint = highPoint == 1 ? 2 : highPoint - 1;
            }

            // Заполняем ключевые точки
            path[0] = open;
            path[highPoint] = high;
            path[lowPoint] = low;
            path[points - 1] = close;

            // Интерполируем между ключевыми точками
            InterpolateSegment(path, 0, highPoint);
            InterpolateSegment(path, highPoint, lowPoint);
            InterpolateSegment(path, lowPoint, points - 1);

            return path;
        }

        /// <summary>
        /// Линейная интерполяция между двумя точками
        /// </summary>
        private void InterpolateSegment(decimal[] path, int start, int end)
        {
            if (start >= end) return;

            decimal startPrice = path[start];
            decimal endPrice = path[end];
            int steps = end - start;

            for (int i = start + 1; i < end; i++)
            {
                decimal ratio = (decimal)(i - start) / steps;
                path[i] = startPrice + (endPrice - startPrice) * ratio;
                
                // Добавляем небольшой шум
                decimal noise = (decimal)(_random.NextDouble() - 0.5) * 0.0001m * startPrice;
                path[i] += noise;
            }
        }

        /// <summary>
        /// Распределяет объем свечи по тикам
        /// </summary>
        private long[] DistributeVolume(long totalVolume, int tickCount)
        {
            var distribution = new long[tickCount];
            
            if (tickCount <= 0) return distribution;

            // Базовое распределение
            long baseVolume = totalVolume / tickCount;
            long remainder = totalVolume % tickCount;

            for (int i = 0; i < tickCount; i++)
            {
                distribution[i] = baseVolume;
                
                // Добавляем остаток к случайным тикам
                if (remainder > 0 && _random.NextDouble() < 0.5)
                {
                    distribution[i]++;
                    remainder--;
                }
            }

            // Добавляем оставшийся остаток к последнему тику
            distribution[tickCount - 1] += remainder;

            // Добавляем вариативность (±20%)
            for (int i = 0; i < tickCount; i++)
            {
                double multiplier = 0.8 + _random.NextDouble() * 0.4; // 0.8 - 1.2
                distribution[i] = Math.Max(1, (long)(distribution[i] * multiplier));
            }

            return distribution;
        }

        /// <summary>
        /// Генерирует уровни Order Book'а
        /// </summary>
        private OrderBookLevel[] GenerateOrderBookSide(decimal startPrice, long startSize, int levels, bool isAsk)
        {
            var side = new OrderBookLevel[levels];
            decimal tickSize = startPrice * 0.00001m; // Минимальный тик
            
            decimal currentPrice = startPrice;
            long currentSize = startSize;

            for (int i = 0; i < levels; i++)
            {
                side[i] = new OrderBookLevel
                {
                    Price = currentPrice,
                    Size = Math.Max(1, currentSize)
                };

                // Следующий уровень
                currentPrice += isAsk ? tickSize * (i + 1) : -tickSize * (i + 1);
                
                // Размер уменьшается с удалением от лучшей цены
                currentSize = (long)(currentSize * (0.7m + (decimal)_random.NextDouble() * 0.3m));
            }

            return side;
        }

        /// <summary>
        /// Определяет направление сделки на основе движения цены
        /// </summary>
        private TradeDirection DetermineTradeDirection(string symbol, decimal currentPrice)
        {
            if (!_lastPrices.ContainsKey(symbol))
                return TradeDirection.Unknown;

            decimal lastPrice = _lastPrices[symbol];
            
            if (currentPrice > lastPrice)
                return TradeDirection.Buy;
            else if (currentPrice < lastPrice)
                return TradeDirection.Sell;
            else
                return TradeDirection.Unknown;
        }

        /// <summary>
        /// Создает тик из текущих рыночных данных (для реального времени)
        /// </summary>
        public TickData CreateRealTimeTick(string symbol, decimal currentPrice, decimal volume24h)
        {
            var timestamp = DateTime.UtcNow;
            
            // Оцениваем объем для одного тика (упрощенно)
            long estimatedVolume = Math.Max(1, (long)(volume24h / (24 * 60 * 60 / 15))); // Примерный объем за 15 секунд
            
            return CreateSimulatedTick(symbol, timestamp, currentPrice, estimatedVolume);
        }

        /// <summary>
        /// Очистка истории для символа
        /// </summary>
        public void ClearHistory(string symbol)
        {
            _lastPrices.Remove(symbol);
            _lastVolumes.Remove(symbol);
        }

        /// <summary>
        /// Очистка всей истории
        /// </summary>
        public void ClearAllHistory()
        {
            _lastPrices.Clear();
            _lastVolumes.Clear();
        }
    }
}
