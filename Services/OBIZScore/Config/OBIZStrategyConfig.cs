using System;
using Microsoft.Extensions.Configuration;

namespace Services.OBIZScore.Config
{
    /// <summary>
    /// Конфигурация OBIZ-Score стратегии
    /// Все параметры вынесены в отдельный класс для удобства настройки
    /// </summary>
    public class OBIZStrategyConfig
    {
        // === ОСНОВНЫЕ ПОРОГИ ===
        
        /// <summary>
        /// Базовый порог Z-Score для входа в позицию
        /// </summary>
        public decimal ZScoreThreshold { get; set; } = 2.0m;

        /// <summary>
        /// Усиленный порог Z-Score для сильных сигналов
        /// </summary>
        public decimal StrongZScoreThreshold { get; set; } = 2.5m;

        /// <summary>
        /// Порог отклонения от VWAP для подтверждения сигнала
        /// </summary>
        public decimal VWAPDeviationThreshold { get; set; } = 1.5m;

        // === ОКНА ДЛЯ РАСЧЕТОВ ===
        
        /// <summary>
        /// Размер окна для расчета Z-Score
        /// </summary>
        public int ZScoreWindow { get; set; } = 100;

        /// <summary>
        /// Размер окна для расчета активности рынка
        /// </summary>
        public int ActivityWindow { get; set; } = 200;

        /// <summary>
        /// Размер окна для расчета Efficiency Ratio
        /// </summary>
        public int EfficiencyWindow { get; set; } = 50;

        /// <summary>
        /// Период сброса VWAP (количество тиков)
        /// </summary>
        public int VWAPResetPeriod { get; set; } = 1000;

        // === ОПРЕДЕЛЕНИЕ РЕЖИМОВ РЫНКА ===
        
        /// <summary>
        /// Порог для определения бокового рынка (choppy)
        /// </summary>
        public decimal ChoppyThreshold { get; set; } = 0.3m;

        /// <summary>
        /// Порог для определения трендового рынка
        /// </summary>
        public decimal TrendingThreshold { get; set; } = 0.7m;

        // === ФИЛЬТР АКТИВНОСТИ ===
        
        /// <summary>
        /// Процентиль для фильтрации по активности (торгуем только выше этого процентиля)
        /// </summary>
        public decimal ActivityPercentileThreshold { get; set; } = 75m;

        /// <summary>
        /// Минимальное количество образцов для расчета процентилей
        /// </summary>
        public int MinSamplesForPercentile { get; set; } = 100;

        // === RISK MANAGEMENT ===
        
        /// <summary>
        /// Базовый Take Profit в долях от цены (1.3 риск-реворд)
        /// </summary>
        public decimal BaseTakeProfit { get; set; } = 0.0013m;

        /// <summary>
        /// Базовый Stop Loss в долях от цены
        /// </summary>
        public decimal BaseStopLoss { get; set; } = 0.001m;

        /// <summary>
        /// Порог PnL для частичного закрытия позиции (80% от TP)
        /// </summary>
        public decimal PartialCloseRatio { get; set; } = 0.8m;

        /// <summary>
        /// Максимальное время удержания позиции в секундах
        /// </summary>
        public int MaxHoldTimeSeconds { get; set; } = 300; // 5 минут

        /// <summary>
        /// Минимальный RR для открытия позиции
        /// </summary>
        public decimal MinRiskReward { get; set; } = 1.0m;

        /// <summary>
        /// Максимальный риск на сделку в долях от депозита
        /// </summary>
        public decimal MaxRiskPerTrade { get; set; } = 0.02m; // 2%

        // === ТЕХНИЧЕСКИЕ ПАРАМЕТРЫ ===
        
        /// <summary>
        /// Максимальный размер истории в памяти
        /// </summary>
        public int MaxHistorySize { get; set; } = 2000;

        /// <summary>
        /// Минимальное количество тиков для начала расчетов
        /// </summary>
        public int MinHistoryForCalculation { get; set; } = 50;

        /// <summary>
        /// Размер Order Book для анализа (количество уровней с каждой стороны)
        /// </summary>
        public int OrderBookDepth { get; set; } = 10;

        /// <summary>
        /// Минимальная волатильность для предотвращения деления на ноль
        /// </summary>
        public decimal MinVolatility { get; set; } = 0.00001m;

        // === АДАПТИВНЫЕ ПАРАМЕТРЫ ===
        
        /// <summary>
        /// Включить адаптивные TP/SL на основе волатильности
        /// </summary>
        public bool EnableAdaptiveLevels { get; set; } = true;

        /// <summary>
        /// Минимальный множитель волатильности для TP/SL
        /// </summary>
        public decimal MinVolatilityMultiplier { get; set; } = 0.5m;

        /// <summary>
        /// Максимальный множитель волатильности для TP/SL
        /// </summary>
        public decimal MaxVolatilityMultiplier { get; set; } = 3.0m;

        // === ФИЛЬТРЫ КАЧЕСТВА ===
        
        /// <summary>
        /// Минимальный размер спреда для торговли (в тиках)
        /// </summary>
        public decimal MinSpreadTicks { get; set; } = 1;

        /// <summary>
        /// Максимальный размер спреда для торговли (в тиках)
        /// </summary>
        public decimal MaxSpreadTicks { get; set; } = 10;

        /// <summary>
        /// Минимальный объем в Order Book для торговли
        /// </summary>
        public long MinOrderBookVolume { get; set; } = 1000;

        // === ЛОГИРОВАНИЕ И ОТЛАДКА ===
        
        /// <summary>
        /// Включить детальное логирование метрик
        /// </summary>
        public bool EnableDetailedLogging { get; set; } = false;

        /// <summary>
        /// Сохранять историю сигналов для анализа
        /// </summary>
        public bool SaveSignalHistory { get; set; } = true;

        /// <summary>
        /// Интервал логирования статистик в секундах
        /// </summary>
        public int LoggingIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Загрузка конфигурации из appsettings.json
        /// </summary>
        public static OBIZStrategyConfig LoadFromConfiguration(IConfiguration configuration)
        {
            var config = new OBIZStrategyConfig();
            configuration.GetSection("OBIZStrategy").Bind(config);
            return config;
        }

        /// <summary>
        /// Валидация параметров конфигурации
        /// </summary>
        public void Validate()
        {
            if (ZScoreThreshold <= 0)
                throw new ArgumentException("ZScoreThreshold must be positive");

            if (StrongZScoreThreshold <= ZScoreThreshold)
                throw new ArgumentException("StrongZScoreThreshold must be greater than ZScoreThreshold");

            if (ZScoreWindow <= 0 || ActivityWindow <= 0 || EfficiencyWindow <= 0)
                throw new ArgumentException("Window sizes must be positive");

            if (ChoppyThreshold >= TrendingThreshold)
                throw new ArgumentException("ChoppyThreshold must be less than TrendingThreshold");

            if (BaseTakeProfit <= 0 || BaseStopLoss <= 0)
                throw new ArgumentException("TP and SL must be positive");

            if (MaxRiskPerTrade <= 0 || MaxRiskPerTrade > 0.1m)
                throw new ArgumentException("MaxRiskPerTrade must be between 0 and 0.1");

            if (MinRiskReward < 0.5m)
                throw new ArgumentException("MinRiskReward should be at least 0.5");
        }

        /// <summary>
        /// Получение строки с основными параметрами для логирования
        /// </summary>
        public override string ToString()
        {
            return $"OBIZ Config: ZScore={ZScoreThreshold}/{StrongZScoreThreshold}, " +
                   $"Windows={ZScoreWindow}/{ActivityWindow}/{EfficiencyWindow}, " +
                   $"TP/SL={BaseTakeProfit:F4}/{BaseStopLoss:F4}, " +
                   $"MaxHold={MaxHoldTimeSeconds}s";
        }
    }
}
