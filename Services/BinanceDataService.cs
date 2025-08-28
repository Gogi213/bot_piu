using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Models;
using Config;

namespace Services
{
    public class BinanceDataService
    {
        private readonly BinanceRestClient _restClient;
        private readonly BackendConfig _config;
        public BinanceDataService(BinanceRestClient restClient, BackendConfig config)
        {
            _restClient = restClient;
            _config = config;
        }

        /// <summary>
        /// Получение всех USDT перпетуальных контрактов с фильтрацией по объему
        /// </summary>
        public async Task<List<CoinData>> GetFilteredUsdtPerpetualsAsync()
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Загрузка USDT перпетуальных контрактов...");

                // Получаем 24h статистику для всех символов
                var tickerResponse = await _restClient.UsdFuturesApi.ExchangeData.GetTickersAsync();
                
                if (!tickerResponse.Success)
                {
                    throw new Exception($"Ошибка получения тикеров: {tickerResponse.Error}");
                }

                var filteredCoins = new List<CoinData>();

                foreach (var ticker in tickerResponse.Data)
                {
                    // Фильтруем только USDT перпетуальные контракты
                    if (!ticker.Symbol.EndsWith("USDT"))
                        continue;

                    // Фильтруем по объему торгов за 24 часа
                    var volume24hUsdt = ticker.QuoteVolume;
                    if (volume24hUsdt < _config.MinVolumeUsdt)
                        continue;

                    var coinData = new CoinData
                    {
                        Symbol = ticker.Symbol,
                        Volume24h = volume24hUsdt,
                        CurrentPrice = ticker.LastPrice,
                        LastUpdated = DateTime.UtcNow,
                        RecentCandles = new List<CandleData>()
                    };

                    filteredCoins.Add(coinData);
                }

                // Сортируем по объему (убывание)
                filteredCoins = filteredCoins
                    .OrderByDescending(c => c.Volume24h)
                    .ToList();

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Найдено {filteredCoins.Count} монет с объемом >{_config.MinVolumeUsdt:N0} USDT");

                return filteredCoins;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка получения данных монет: {ex.Message}");
                return new List<CoinData>();
            }
        }

        /// <summary>
        /// Загрузка исторических свечей для символа
        /// </summary>
        public async Task<List<CandleData>> GetHistoricalCandlesAsync(string symbol, int candleCount = 35)
        {
            try
            {
                var candlesResponse = await _restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol: symbol,
                    interval: KlineInterval.OneMinute,
                    limit: candleCount
                );

                if (!candlesResponse.Success)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка загрузки свечей для {symbol}: {candlesResponse.Error}");
                    return new List<CandleData>();
                }

                var candles = candlesResponse.Data.Select(k => new CandleData
                {
                    OpenTime = k.OpenTime,
                    Open = k.OpenPrice,
                    High = k.HighPrice,
                    Low = k.LowPrice,
                    Close = k.ClosePrice,
                    Volume = k.Volume
                }).ToList();

                return candles;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка загрузки свечей для {symbol}: {ex.Message}");
                return new List<CandleData>();
            }
        }

        /// <summary>
        /// Пакетная загрузка исторических данных для списка монет
        /// </summary>
        public async Task<Dictionary<string, List<CandleData>>> GetBatchHistoricalDataAsync(List<string> symbols, int candleCount = 35)
        {
            var result = new Dictionary<string, List<CandleData>>();
            var batchSize = 40; // Ограничиваем количество одновременных запросов
            
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📈 Загрузка исторических данных для {symbols.Count} символов (пакеты по {batchSize})...");

            for (int i = 0; i < symbols.Count; i += batchSize)
            {
                var batch = symbols.Skip(i).Take(batchSize).ToList();
                var tasks = batch.Select(async symbol =>
                {
                    var candles = await GetHistoricalCandlesAsync(symbol, candleCount);
                    return new { Symbol = symbol, Candles = candles };
                });

                var batchResults = await Task.WhenAll(tasks);
                
                foreach (var item in batchResults)
                {
                    result[item.Symbol] = item.Candles;
                }

                // Небольшая задержка между пакетами для избежания лимитов
                if (i + batchSize < symbols.Count)
                {
                    await Task.Delay(100);
                }

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Обработано {Math.Min(i + batchSize, symbols.Count)}/{symbols.Count} символов");
            }

            var successCount = result.Values.Count(candles => candles.Count > 0);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Загружены исторические данные: {successCount}/{symbols.Count} символов");

            return result;
        }

        /// <summary>
        /// Получение текущей цены символа
        /// </summary>
        public async Task<decimal?> GetCurrentPriceAsync(string symbol)
        {
            try
            {
                var priceResponse = await _restClient.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol);
                
                if (!priceResponse.Success)
                    return null;

                return priceResponse.Data.Price;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Получение информации о символе (tick size, min quantity и т.д.)
        /// </summary>
        public async Task<decimal?> GetTickSizeAsync(string symbol)
        {
            try
            {
                var exchangeInfoResponse = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
                if (!exchangeInfoResponse.Success)
                    return null;

                var symbolInfo = exchangeInfoResponse.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolInfo == null)
                    return null;

                var priceFilter = symbolInfo.PriceFilter;
                return priceFilter?.TickSize;
            }
            catch
            {
                return null;
            }
        }
    }
}
