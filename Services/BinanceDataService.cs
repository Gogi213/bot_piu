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

                // Добавляем задержку для предотвращения rate limit
                await Task.Delay(1000);

                // Получаем 24h статистику для всех символов
                var tickerResponse = await _restClient.UsdFuturesApi.ExchangeData.GetTickersAsync();
                
                if (!tickerResponse.Success)
                {
                    var errorMsg = tickerResponse.Error?.ToString() ?? "Unknown error";
                    if (errorMsg.Contains("403") || errorMsg.Contains("Forbidden"))
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Rate limit достигнут, ожидание 30 секунд...");
                        await Task.Delay(30000);
                        
                        // Повторная попытка
                        tickerResponse = await _restClient.UsdFuturesApi.ExchangeData.GetTickersAsync();
                        if (!tickerResponse.Success)
                        {
                            throw new Exception($"Ошибка получения тикеров после retry: {tickerResponse.Error}");
                        }
                    }
                    else
                    {
                        throw new Exception($"Ошибка получения тикеров: {tickerResponse.Error}");
                    }
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
        /// Получение информации о конкретном символе
        /// </summary>
        public async Task<CoinTickerData?> GetSymbolTickerAsync(string symbol)
        {
            try
            {
                var tickerResponse = await _restClient.UsdFuturesApi.ExchangeData.GetTickerAsync(symbol);
                
                if (!tickerResponse.Success)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] ⚠️ Символ {symbol} не найден: {tickerResponse.Error}");
                    return null;
                }

                var ticker = tickerResponse.Data;
                return new CoinTickerData
                {
                    Symbol = ticker.Symbol,
                    Price = ticker.LastPrice,
                    QuoteVolume = ticker.QuoteVolume,
                    PriceChangePercent = ticker.PriceChangePercent
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] ❌ Ошибка получения данных для {symbol}: {ex.Message}");
                return null;
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

                // Убрали задержку между батчами

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
                // Сначала пробуем получить через GetExchangeInfoAsync
                var exchangeInfoResponse = await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync();
                if (exchangeInfoResponse.Success && exchangeInfoResponse.Data?.Symbols != null)
                {
                    var symbolInfo = exchangeInfoResponse.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                    if (symbolInfo?.PriceFilter?.TickSize != null)
                    {
                        JsonLogger.Debug("BINANCE_DATA", "TickSize retrieved from ExchangeInfo", new Dictionary<string, object>
                        {
                            ["symbol"] = symbol,
                            ["tickSize"] = symbolInfo.PriceFilter.TickSize
                        });
                        return symbolInfo.PriceFilter.TickSize;
                    }
                }
                else
                {
                    JsonLogger.Warning("BINANCE_DATA", "ExchangeInfo request failed", new Dictionary<string, object>
                    {
                        ["symbol"] = symbol,
                        ["error"] = exchangeInfoResponse.Error?.ToString() ?? "Unknown error",
                        ["fallbackToSmartTickSize"] = true
                    });
                }

                // Fallback: умный расчет TickSize на основе цены
                return await GetSmartTickSizeAsync(symbol);
            }
            catch (Exception ex)
            {
                JsonLogger.Error("BINANCE_DATA", "Failed to get TickSize", new Dictionary<string, object>
                {
                    ["symbol"] = symbol,
                    ["fallbackToSmartTickSize"] = true
                }, ex);
                
                // Fallback: умный расчет TickSize
                return await GetSmartTickSizeAsync(symbol);
            }
        }

        /// <summary>
        /// Умный расчет TickSize на основе цены символа и популярных паттернов Binance
        /// </summary>
        private async Task<decimal> GetSmartTickSizeAsync(string symbol)
        {
            try
            {
                // Получаем текущую цену
                var currentPrice = await GetCurrentPriceAsync(symbol);
                if (!currentPrice.HasValue || currentPrice.Value <= 0)
                {
                    JsonLogger.Warning("BINANCE_DATA", "Could not get current price for smart TickSize", new Dictionary<string, object>
                    {
                        ["symbol"] = symbol,
                        ["defaultTickSize"] = 0.0001m
                    });
                    return 0.0001m; // Безопасное значение по умолчанию
                }

                var price = currentPrice.Value;
                decimal smartTickSize;

                // Умная логика на основе цены и паттернов Binance
                if (price >= 1000)
                    smartTickSize = 1m;           // BTCUSDT и т.д.
                else if (price >= 100)
                    smartTickSize = 0.1m;         // ETHUSDT и т.д.
                else if (price >= 10)
                    smartTickSize = 0.01m;        // BNBUSDT и т.д.
                else if (price >= 1)
                    smartTickSize = 0.001m;       // ADAUSDT и т.д.
                else if (price >= 0.1m)
                    smartTickSize = 0.0001m;      // DOGEUSDT и т.д.
                else if (price >= 0.01m)
                    smartTickSize = 0.00001m;     // SHIBUSDT и т.д.
                else
                    smartTickSize = 0.000001m;    // Очень дешевые монеты

                JsonLogger.Info("BINANCE_DATA", "Smart TickSize calculated", new Dictionary<string, object>
                {
                    ["symbol"] = symbol,
                    ["currentPrice"] = price,
                    ["smartTickSize"] = smartTickSize,
                    ["method"] = "price-based-calculation"
                });

                return smartTickSize;
            }
            catch (Exception ex)
            {
                JsonLogger.Error("BINANCE_DATA", "Smart TickSize calculation failed", new Dictionary<string, object>
                {
                    ["symbol"] = symbol,
                    ["defaultTickSize"] = 0.0001m
                }, ex);

                return 0.0001m; // Безопасное значение по умолчанию
            }
        }

        /// <summary>
        /// Получение реальных позиций с биржи Binance
        /// </summary>
        public async Task<Dictionary<string, BinancePosition>> GetRealPositionsAsync()
        {
            try
            {
                var positionsResponse = await _restClient.UsdFuturesApi.Account.GetPositionInformationAsync();
                if (!positionsResponse.Success)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка получения позиций: {positionsResponse.Error}");
                    return new Dictionary<string, BinancePosition>();
                }

                var realPositions = new Dictionary<string, BinancePosition>();
                
                foreach (var position in positionsResponse.Data)
                {
                    // Только активные позиции (размер != 0)
                    if (position.Quantity != 0)
                    {
                        realPositions[position.Symbol] = new BinancePosition
                        {
                            Symbol = position.Symbol,
                            Side = position.Quantity > 0 ? "BUY" : "SELL",
                            Quantity = Math.Abs(position.Quantity),
                            EntryPrice = position.EntryPrice,
                            MarkPrice = position.MarkPrice,
                            PnL = position.UnrealizedPnl
                        };
                    }
                }

                return realPositions;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка получения реальных позиций: {ex.Message}");
                return new Dictionary<string, BinancePosition>();
            }
        }
    }

    /// <summary>
    /// Упрощенная информация о позиции с биржи
    /// </summary>
    public class BinancePosition
    {
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }
        public decimal PnL { get; set; }
    }

    /// <summary>
    /// Данные тикера для конкретного символа
    /// </summary>
    public class CoinTickerData
    {
        public string Symbol { get; set; } = "";
        public decimal Price { get; set; }
        public decimal QuoteVolume { get; set; }
        public decimal PriceChangePercent { get; set; }
    }
}
