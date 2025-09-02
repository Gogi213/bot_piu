using System;
using System.Linq;

namespace Services.OBIZScore.Core
{
    /// <summary>
    /// Быстрые расчеты статистик на скользящем окне
    /// Инкрементальное обновление для максимальной производительности
    /// </summary>
    public class RollingStatistics
    {
        private readonly CircularBuffer<decimal> _values;
        private readonly int _windowSize;
        private decimal _sum;


        public RollingStatistics(int windowSize)
        {
            if (windowSize <= 0)
                throw new ArgumentException("Window size must be greater than 0", nameof(windowSize));
                
            _windowSize = windowSize;
            _values = new CircularBuffer<decimal>(windowSize);
            _sum = 0;
            // Initialized
        }

        /// <summary>
        /// Добавляет новое значение и обновляет статистики
        /// </summary>
        public void Add(decimal value)
        {
            // Если буфер полный, убираем старое значение из суммы
            if (_values.IsFull)
            {
                _sum -= _values.First();
                // Value removed
            }
            
            _values.Add(value);
            _sum += value;
        }

        /// <summary>
        /// Количество значений в окне
        /// </summary>
        public int Count => _values.Count;

        /// <summary>
        /// Среднее значение
        /// </summary>
        public decimal Mean => Count > 0 ? _sum / Count : 0;

        /// <summary>
        /// Стандартное отклонение
        /// </summary>
        public decimal StandardDeviation
        {
            get
            {
                if (Count <= 1) return 0;
                
                var mean = Mean;
                var variance = _values.Sum(x => (x - mean) * (x - mean)) / Count;
                return (decimal)Math.Sqrt((double)variance);
            }
        }

        /// <summary>
        /// Дисперсия
        /// </summary>
        public decimal Variance
        {
            get
            {
                if (Count <= 1) return 0;
                
                var mean = Mean;
                return _values.Sum(x => (x - mean) * (x - mean)) / Count;
            }
        }

        /// <summary>
        /// Минимальное значение в окне
        /// </summary>
        public decimal Min => Count > 0 ? _values.Min() : 0;

        /// <summary>
        /// Максимальное значение в окне
        /// </summary>
        public decimal Max => Count > 0 ? _values.Max() : 0;

        /// <summary>
        /// Расчет процентиля
        /// </summary>
        public decimal GetPercentile(decimal percentile)
        {
            if (Count == 0) return 0;
            
            if (percentile < 0 || percentile > 100)
                throw new ArgumentException("Percentile must be between 0 and 100", nameof(percentile));
            
            var sorted = _values.OrderBy(x => x).ToArray();
            
            if (percentile == 0) return sorted[0];
            if (percentile == 100) return sorted[^1];
            
            // Линейная интерполяция
            var index = percentile / 100m * (sorted.Length - 1);
            var lowerIndex = (int)Math.Floor(index);
            var upperIndex = (int)Math.Ceiling(index);
            
            if (lowerIndex == upperIndex)
                return sorted[lowerIndex];
            
            var weight = (decimal)(index - lowerIndex);
            return sorted[lowerIndex] * (1 - weight) + sorted[upperIndex] * weight;
        }

        /// <summary>
        /// Медиана (50-й процентиль)
        /// </summary>
        public decimal Median => GetPercentile(50);

        /// <summary>
        /// Проверяет, достаточно ли данных для надежных расчетов
        /// </summary>
        public bool HasSufficientData(int minimumSamples = 10)
        {
            return Count >= minimumSamples;
        }

        /// <summary>
        /// Очищает все статистики
        /// </summary>
        public void Clear()
        {
            _values.Clear();
            _sum = 0;
            // Initialized
        }

        /// <summary>
        /// Z-Score для значения относительно текущего окна
        /// </summary>
        public decimal CalculateZScore(decimal value)
        {
            var stdDev = StandardDeviation;
            return stdDev > 0 ? (value - Mean) / stdDev : 0;
        }

        /// <summary>
        /// Проверяет, является ли значение выбросом (за пределами N стандартных отклонений)
        /// </summary>
        public bool IsOutlier(decimal value, decimal sigmaThreshold = 2.0m)
        {
            return Math.Abs(CalculateZScore(value)) > sigmaThreshold;
        }
    }
}
