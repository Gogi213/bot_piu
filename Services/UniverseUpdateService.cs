using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Models;
using Config;

namespace Services
{
    public class UniverseUpdateService
    {
        private readonly BinanceDataService _binanceService;
        private readonly DataStorageService _dataStorage;
        private readonly BackendConfig _config;

        public UniverseUpdateService(
            BinanceDataService binanceService, 
            DataStorageService dataStorage, 
            BackendConfig config)
        {
            _binanceService = binanceService;
            _dataStorage = dataStorage;
            _config = config;
        }

        /// <summary>
        /// Полное обновление пула монет: получение, фильтрация, загрузка истории, расчет NATR
        /// </summary>
        public async Task<UniverseUpdateResult> UpdateUniverseAsync()
        {
            var result = new UniverseUpdateResult
            {
                StartTime = DateTime.UtcNow
            };

            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 Начинаем обновление пула монет...");

                // Шаг 1: Получаем все USDT перпетуальные контракты с фильтрацией по объему
                var filteredCoins = await _binanceService.GetFilteredUsdtPerpetualsAsync();
                result.TotalCoinsFound = filteredCoins.Count;

                if (filteredCoins.Count == 0)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Не найдено монет с объемом >{_config.MinVolumeUsdt:N0} USDT");
                    result.Success = false;
                    result.ErrorMessage = "Не найдено подходящих монет";
                    return result;
                }

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Найдено {filteredCoins.Count} монет. Загружаем исторические данные...");

                // Шаг 2: Загружаем исторические данные для всех монет
                var symbols = filteredCoins.Select(c => c.Symbol).ToList();
                var historicalData = await _binanceService.GetBatchHistoricalDataAsync(symbols, _config.HistoryCandles);

                // Шаг 3: Обновляем данные монет историческими свечами и рассчитываем NATR
                int coinsWithNatr = 0;
                foreach (var coin in filteredCoins)
                {
                    if (historicalData.TryGetValue(coin.Symbol, out var candles) && candles.Count > 0)
                    {
                        coin.RecentCandles = candles;
                        coin.Natr = TechnicalAnalysisService.CalculateNatr(candles, _config.NatrPeriods);
                        
                        if (coin.Natr.HasValue)
                        {
                            coinsWithNatr++;
                        }
                    }

                    // Сохраняем в хранилище
                    _dataStorage.UpdateCoinData(coin.Symbol, coin);
                }

                // Шаг 4: Фильтруем по NATR и получаем финальный список
                var finalCoins = _dataStorage.GetFilteredCoins(_config.MinVolumeUsdt, _config.MinNatrPercent);

                result.CoinsWithHistory = historicalData.Values.Count(candles => candles.Count > 0);
                result.CoinsWithNatr = coinsWithNatr;
                result.FinalFilteredCoins = finalCoins.Count;
                result.Success = true;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime.Value - result.StartTime;

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Обновление пула завершено:");
                Console.WriteLine($"    📈 Всего найдено: {result.TotalCoinsFound} монет");
                Console.WriteLine($"    📊 С историческими данными: {result.CoinsWithHistory} монет");
                Console.WriteLine($"    🧮 С рассчитанным NATR: {result.CoinsWithNatr} монет");
                Console.WriteLine($"    ✨ Финальный фильтр (NATR ≥ {_config.MinNatrPercent}%): {result.FinalFilteredCoins} монет");
                Console.WriteLine($"    ⏱️ Время выполнения: {result.Duration.TotalSeconds:F1} сек");

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime.Value - result.StartTime;

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка обновления пула: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Получение превью пула с подробной информацией
        /// </summary>
        public UniversePreview GetUniversePreview()
        {
            var allCoins = _dataStorage.GetAllCoins();
            var filteredCoins = _dataStorage.GetFilteredCoins(_config.MinVolumeUsdt, _config.MinNatrPercent);

            var preview = new UniversePreview
            {
                TotalCoins = allCoins.Count,
                FilteredCoins = filteredCoins.Count,
                LastUpdate = allCoins.Any() ? allCoins.Max(c => c.LastUpdated) : (DateTime?)null,
                TopCoinsByVolume = allCoins
                    .OrderByDescending(c => c.Volume24h)
                    .Take(10)
                    .Select(c => new CoinSummary
                    {
                        Symbol = c.Symbol,
                        Volume24h = c.Volume24h,
                        CurrentPrice = c.CurrentPrice,
                        Natr = c.Natr
                    }).ToList(),
                TopCoinsByNatr = filteredCoins
                    .Where(c => c.Natr.HasValue)
                    .OrderByDescending(c => c.Natr!.Value)
                    .Take(10)
                    .Select(c => new CoinSummary
                    {
                        Symbol = c.Symbol,
                        Volume24h = c.Volume24h,
                        CurrentPrice = c.CurrentPrice,
                        Natr = c.Natr
                    }).ToList()
            };

            return preview;
        }
    }

    public class UniverseUpdateResult
    {
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int TotalCoinsFound { get; set; }
        public int CoinsWithHistory { get; set; }
        public int CoinsWithNatr { get; set; }
        public int FinalFilteredCoins { get; set; }
    }

    public class UniversePreview
    {
        public int TotalCoins { get; set; }
        public int FilteredCoins { get; set; }
        public DateTime? LastUpdate { get; set; }
        public List<CoinSummary> TopCoinsByVolume { get; set; } = new();
        public List<CoinSummary> TopCoinsByNatr { get; set; } = new();
    }

    public class CoinSummary
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal Volume24h { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal? Natr { get; set; }
    }
}

