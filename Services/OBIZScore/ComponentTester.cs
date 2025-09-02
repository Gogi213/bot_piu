using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Services.OBIZScore.Core;
using Services.OBIZScore.Config;
using Models;
using Binance.Net.Clients;
using CryptoExchange.Net.Authentication;
using static Services.OBIZScore.Core.TradeDirection;

namespace Services.OBIZScore
{
    /// <summary>
    /// Тестер отдельных компонентов OBIZ стратегии
    /// </summary>
    public class ComponentTester : IDisposable
    {
        private readonly BinanceRestClient _restClient;
        private readonly BinanceSocketClient _socketClient;
        private readonly OBIZStrategyConfig _config;

        public ComponentTester()
        {
            // Инициализация клиентов (используем публичные эндпоинты для тестов)
            _restClient = new BinanceRestClient();
            _socketClient = new BinanceSocketClient();
            
            // Тестовая конфигурация
            _config = new OBIZStrategyConfig
            {
                ZScoreThreshold = 2.0m,
                StrongZScoreThreshold = 2.5m,
                ZScoreWindow = 50,
                ActivityWindow = 100,
                OrderBookDepth = 10,
                EnableDetailedLogging = true
            };
        }

        /// <summary>
        /// Тест торговых данных (Trades)
        /// </summary>
        public async Task<bool> TestTradesAsync(string symbol = "ETHUSDT")
        {
            Console.WriteLine("🔄 ТЕСТ TRADES DATA");
            Console.WriteLine("==================");
            
            try
            {
                Console.WriteLine($"📊 Загрузка последних трейдов для {symbol}...");
                
                // Получаем последние трейды
                var tradesResponse = await _restClient.SpotApi.ExchangeData.GetRecentTradesAsync(symbol, 100);
                
                if (!tradesResponse.Success)
                {
                    Console.WriteLine($"❌ Ошибка получения трейдов: {tradesResponse.Error}");
                    return false;
                }

                var trades = tradesResponse.Data.ToList();
                Console.WriteLine($"✅ Получено {trades.Count} трейдов");

                // Анализируем трейды
                var buyTrades = trades.Where(t => !t.BuyerIsMaker).ToList();
                var sellTrades = trades.Where(t => t.BuyerIsMaker).ToList();
                
                var totalVolume = trades.Sum(t => t.BaseQuantity);
                var buyVolume = buyTrades.Sum(t => t.BaseQuantity);
                var sellVolume = sellTrades.Sum(t => t.BaseQuantity);
                
                var avgPrice = trades.Average(t => t.Price);
                var priceRange = trades.Max(t => t.Price) - trades.Min(t => t.Price);
                
                Console.WriteLine("📈 АНАЛИЗ ТРЕЙДОВ:");
                Console.WriteLine($"   Общий объем: {totalVolume:F2}");
                Console.WriteLine($"   Buy объем: {buyVolume:F2} ({buyVolume/totalVolume:P1})");
                Console.WriteLine($"   Sell объем: {sellVolume:F2} ({sellVolume/totalVolume:P1})");
                Console.WriteLine($"   Средняя цена: {avgPrice:F4}");
                Console.WriteLine($"   Ценовой диапазон: {priceRange:F4}");
                Console.WriteLine($"   Buy/Sell ratio: {(buyVolume/sellVolume):F2}");

                // Симулируем OBIZ метрики из трейдов
                var imbalance = (buyVolume - sellVolume) / totalVolume;
                var activity = totalVolume;
                
                Console.WriteLine("🧠 OBIZ МЕТРИКИ:");
                Console.WriteLine($"   Trade Imbalance: {imbalance:F4} ({(imbalance > 0 ? "Buy pressure" : "Sell pressure")})");
                Console.WriteLine($"   Activity Score: {activity:F2}");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 Ошибка тестирования трейдов: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Тест Order Book данных
        /// </summary>
        public async Task<bool> TestOrderBookAsync(string symbol = "ETHUSDT")
        {
            Console.WriteLine("\n🔄 ТЕСТ ORDER BOOK DATA");
            Console.WriteLine("=======================");
            
            try
            {
                Console.WriteLine($"📊 Загрузка Order Book для {symbol} (глубина: {_config.OrderBookDepth})...");
                
                // Получаем Order Book
                var orderBookResponse = await _restClient.SpotApi.ExchangeData.GetOrderBookAsync(symbol, _config.OrderBookDepth);
                
                if (!orderBookResponse.Success)
                {
                    Console.WriteLine($"❌ Ошибка получения Order Book: {orderBookResponse.Error}");
                    return false;
                }

                var orderBook = orderBookResponse.Data;
                Console.WriteLine($"✅ Получен Order Book: {orderBook.Bids.Count()} bids, {orderBook.Asks.Count()} asks");

                // Анализируем Order Book
                var bids = orderBook.Bids.Take(_config.OrderBookDepth).ToList();
                var asks = orderBook.Asks.Take(_config.OrderBookDepth).ToList();
                
                var totalBidVolume = bids.Sum(b => b.Quantity);
                var totalAskVolume = asks.Sum(a => a.Quantity);
                var totalVolume = totalBidVolume + totalAskVolume;
                
                var bestBid = bids.First().Price;
                var bestAsk = asks.First().Price;
                var spread = bestAsk - bestBid;
                var midPrice = (bestBid + bestAsk) / 2;
                
                // Вычисляем OBIZ Score (упрощенно)
                var imbalance = (totalBidVolume - totalAskVolume) / totalVolume;
                var obizScore = imbalance * 10; // Упрощенная нормализация
                
                Console.WriteLine("📈 ORDER BOOK АНАЛИЗ:");
                Console.WriteLine($"   Best Bid: {bestBid:F4}");
                Console.WriteLine($"   Best Ask: {bestAsk:F4}");
                Console.WriteLine($"   Spread: {spread:F4} ({spread/midPrice:P3})");
                Console.WriteLine($"   Mid Price: {midPrice:F4}");
                Console.WriteLine();
                Console.WriteLine($"   Total Bid Volume: {totalBidVolume:F2}");
                Console.WriteLine($"   Total Ask Volume: {totalAskVolume:F2}");
                Console.WriteLine($"   Volume Ratio: {totalBidVolume/totalAskVolume:F2}");
                
                Console.WriteLine("🧠 OBIZ МЕТРИКИ:");
                Console.WriteLine($"   Order Book Imbalance: {imbalance:F4}");
                Console.WriteLine($"   OBIZ Score: {obizScore:F2}");
                Console.WriteLine($"   Сигнал: {(Math.Abs(obizScore) > _config.ZScoreThreshold ? (obizScore > 0 ? "BUY" : "SELL") : "FLAT")}");

                // Детализация по уровням
                Console.WriteLine("\n📊 ДЕТАЛИЗАЦИЯ ПО УРОВНЯМ:");
                Console.WriteLine("BIDS:");
                for (int i = 0; i < Math.Min(5, bids.Count); i++)
                {
                    var bid = bids[i];
                    var distance = (midPrice - bid.Price) / midPrice;
                    Console.WriteLine($"   {i+1}. {bid.Price:F4} | {bid.Quantity:F2} | {distance:P3}");
                }
                
                Console.WriteLine("ASKS:");
                for (int i = 0; i < Math.Min(5, asks.Count); i++)
                {
                    var ask = asks[i];
                    var distance = (ask.Price - midPrice) / midPrice;
                    Console.WriteLine($"   {i+1}. {ask.Price:F4} | {ask.Quantity:F2} | {distance:P3}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 Ошибка тестирования Order Book: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Тест WebSocket подключений
        /// </summary>
        public async Task<bool> TestWebSocketAsync(string symbol = "ETHUSDT", int durationSeconds = 30)
        {
            Console.WriteLine("\n🔄 ТЕСТ WEBSOCKET CONNECTIONS");
            Console.WriteLine("=============================");
            
            try
            {
                Console.WriteLine($"📡 Подписка на WebSocket данные для {symbol}...");
                Console.WriteLine($"⏱️ Тест будет длиться {durationSeconds} секунд");
                
                var priceUpdates = 0;
                var tradeUpdates = 0;
                var orderBookUpdates = 0;
                
                var lastPrice = 0m;
                var priceChanges = new List<decimal>();
                
                // Подписка на цены (ticker)
                var priceSubscription = await _socketClient.SpotApi.ExchangeData.SubscribeToTickerUpdatesAsync(symbol, data =>
                {
                    priceUpdates++;
                    if (lastPrice > 0)
                    {
                        var change = data.Data.LastPrice - lastPrice;
                        priceChanges.Add(change);
                    }
                    lastPrice = data.Data.LastPrice;
                    
                    if (priceUpdates % 10 == 0)
                    {
                        Console.WriteLine($"💰 Price Update #{priceUpdates}: {data.Data.LastPrice:F4}");
                    }
                });

                if (!priceSubscription.Success)
                {
                    Console.WriteLine($"❌ Ошибка подписки на цены: {priceSubscription.Error}");
                    return false;
                }

                // Подписка на трейды
                var tradeSubscription = await _socketClient.SpotApi.ExchangeData.SubscribeToTradeUpdatesAsync(symbol, data =>
                {
                    tradeUpdates++;
                    if (tradeUpdates % 5 == 0)
                    {
                        var direction = data.Data.BuyerIsMaker ? "SELL" : "BUY";
                        Console.WriteLine($"📈 Trade #{tradeUpdates}: {direction} {data.Data.Quantity:F2} @ {data.Data.Price:F4}");
                    }
                });

                if (!tradeSubscription.Success)
                {
                    Console.WriteLine($"❌ Ошибка подписки на трейды: {tradeSubscription.Error}");
                    await priceSubscription.Data.CloseAsync();
                    return false;
                }

                // Подписка на Order Book
                var orderBookSubscription = await _socketClient.SpotApi.ExchangeData.SubscribeToOrderBookUpdatesAsync(symbol, 100, data =>
                {
                    orderBookUpdates++;
                    if (orderBookUpdates % 20 == 0)
                    {
                        var bestBid = data.Data.Bids.FirstOrDefault()?.Price ?? 0;
                        var bestAsk = data.Data.Asks.FirstOrDefault()?.Price ?? 0;
                        var spread = bestAsk - bestBid;
                        Console.WriteLine($"📊 OrderBook #{orderBookUpdates}: Spread {spread:F4}");
                    }
                });

                if (!orderBookSubscription.Success)
                {
                    Console.WriteLine($"❌ Ошибка подписки на Order Book: {orderBookSubscription.Error}");
                    await priceSubscription.Data.CloseAsync();
                    await tradeSubscription.Data.CloseAsync();
                    return false;
                }

                Console.WriteLine("✅ Все WebSocket подписки активны!");
                Console.WriteLine("🔄 Мониторинг данных...");

                // Ждем указанное время
                var startTime = DateTime.UtcNow;
                while ((DateTime.UtcNow - startTime).TotalSeconds < durationSeconds)
                {
                    await Task.Delay(5000);
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    Console.WriteLine($"⏱️ {elapsed:F0}s | Prices: {priceUpdates}, Trades: {tradeUpdates}, OrderBook: {orderBookUpdates}");
                }

                // Закрываем подписки
                await priceSubscription.Data.CloseAsync();
                await tradeSubscription.Data.CloseAsync();
                await orderBookSubscription.Data.CloseAsync();

                // Анализ результатов
                Console.WriteLine("\n📊 РЕЗУЛЬТАТЫ WEBSOCKET ТЕСТА:");
                Console.WriteLine($"   Обновления цен: {priceUpdates}");
                Console.WriteLine($"   Обновления трейдов: {tradeUpdates}");
                Console.WriteLine($"   Обновления Order Book: {orderBookUpdates}");
                Console.WriteLine($"   Скорость цен: {priceUpdates/durationSeconds:F1} обновлений/сек");
                Console.WriteLine($"   Скорость трейдов: {tradeUpdates/durationSeconds:F1} трейдов/сек");
                Console.WriteLine($"   Скорость Order Book: {orderBookUpdates/durationSeconds:F1} обновлений/сек");

                if (priceChanges.Count > 0)
                {
                    var avgChange = priceChanges.Average();
                    var maxChange = priceChanges.Max();
                    var minChange = priceChanges.Min();
                    Console.WriteLine($"   Средний тик: {avgChange:F6}");
                    Console.WriteLine($"   Макс тик: {maxChange:F6}");
                    Console.WriteLine($"   Мин тик: {minChange:F6}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 Ошибка тестирования WebSocket: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Тест интеграции всех компонентов
        /// </summary>
        public async Task<bool> TestIntegrationAsync(string symbol = "ETHUSDT")
        {
            Console.WriteLine("\n🔄 ТЕСТ ИНТЕГРАЦИИ КОМПОНЕНТОВ");
            Console.WriteLine("==============================");
            
            try
            {
                Console.WriteLine("🧠 Создание OBIZ стратегии...");
                var strategy = new OBIZScoreStrategy(_config);
                
                Console.WriteLine("📊 Получение начальных данных...");
                
                // Получаем Order Book
                var orderBookResponse = await _restClient.SpotApi.ExchangeData.GetOrderBookAsync(symbol, _config.OrderBookDepth);
                if (!orderBookResponse.Success)
                {
                    Console.WriteLine($"❌ Ошибка получения Order Book: {orderBookResponse.Error}");
                    return false;
                }

                // Получаем трейды
                var tradesResponse = await _restClient.SpotApi.ExchangeData.GetRecentTradesAsync(symbol, 50);
                if (!tradesResponse.Success)
                {
                    Console.WriteLine($"❌ Ошибка получения трейдов: {tradesResponse.Error}");
                    return false;
                }

                var trades = tradesResponse.Data.ToList();
                var orderBook = orderBookResponse.Data;
                
                Console.WriteLine("🔄 Симуляция тиков для прогрева стратегии...");
                
                // Прогреваем стратегию симулированными тиками
                for (int i = 0; i < 100; i++)
                {
                    var trade = trades[i % trades.Count];
                    var tick = new TickData
                    {
                        Timestamp = DateTime.UtcNow.AddMilliseconds(-i * 100),
                        Price = trade.Price,
                        Volume = (long)trade.BaseQuantity,
                        BestBid = trade.Price * 0.9999m, // Симуляция bid
                        BestAsk = trade.Price * 1.0001m, // Симуляция ask
                        BidSize = 1000,
                        AskSize = 1000,
                        Direction = trade.BuyerIsMaker ? TradeDirection.Sell : TradeDirection.Buy,
                        Bids = new OrderBookLevel[]
                        {
                            new OrderBookLevel { Price = trade.Price * 0.9999m, Size = 1000 },
                            new OrderBookLevel { Price = trade.Price * 0.9998m, Size = 500 },
                            new OrderBookLevel { Price = trade.Price * 0.9997m, Size = 300 }
                        },
                        Asks = new OrderBookLevel[]
                        {
                            new OrderBookLevel { Price = trade.Price * 1.0001m, Size = 1200 },
                            new OrderBookLevel { Price = trade.Price * 1.0002m, Size = 800 },
                            new OrderBookLevel { Price = trade.Price * 1.0003m, Size = 400 }
                        }
                    };

                    var decision = await strategy.ProcessTickAsync(tick, symbol);
                    
                    if (i % 20 == 0)
                    {
                        var stats = strategy.GetCurrentStats();
                        Console.WriteLine($"📈 Тик {i+1}: OBIZ={stats.CurrentOBIZScore:F2}, Activity={stats.CurrentActivityScore:F2}, Режим={stats.CurrentRegime}");
                    }
                }

                var finalStats = strategy.GetCurrentStats();
                Console.WriteLine("\n🎯 ФИНАЛЬНЫЕ РЕЗУЛЬТАТЫ ИНТЕГРАЦИИ:");
                Console.WriteLine($"   OBIZ Score: {finalStats.CurrentOBIZScore:F2}");
                Console.WriteLine($"   Activity Score: {finalStats.CurrentActivityScore:F2}");
                Console.WriteLine($"   Efficiency Ratio: {finalStats.CurrentEfficiencyRatio:F2}");
                Console.WriteLine($"   VWAP Deviation: {finalStats.CurrentVWAPDeviation:F2}");
                Console.WriteLine($"   Market Regime: {finalStats.CurrentRegime}");
                Console.WriteLine($"   Готовность стратегии: {(finalStats.HasSufficientData ? "✅" : "❌")}");
                Console.WriteLine($"   Обработано тиков: {finalStats.TicksProcessed}");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 Ошибка интеграционного теста: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Запуск всех тестов
        /// </summary>
        public async Task RunAllTestsAsync(string symbol = "ETHUSDT")
        {
            Console.WriteLine("🚀 ЗАПУСК ПОЛНОГО ТЕСТИРОВАНИЯ OBIZ КОМПОНЕНТОВ");
            Console.WriteLine("===============================================");
            Console.WriteLine($"🎯 Тестируемый символ: {symbol}");
            Console.WriteLine($"⚙️ Конфигурация: {_config}");
            Console.WriteLine();

            var results = new Dictionary<string, bool>();

            // Тест трейдов
            results["Trades"] = await TestTradesAsync(symbol);
            
            // Тест Order Book
            results["OrderBook"] = await TestOrderBookAsync(symbol);
            
            // Тест WebSocket (сокращенный)
            results["WebSocket"] = await TestWebSocketAsync(symbol, 15);
            
            // Тест интеграции
            results["Integration"] = await TestIntegrationAsync(symbol);

            // Итоговые результаты
            Console.WriteLine("\n🏁 ИТОГОВЫЕ РЕЗУЛЬТАТЫ ТЕСТИРОВАНИЯ");
            Console.WriteLine("===================================");
            
            foreach (var result in results)
            {
                var status = result.Value ? "✅ УСПЕХ" : "❌ ОШИБКА";
                Console.WriteLine($"   {result.Key}: {status}");
            }

            var successCount = results.Values.Count(r => r);
            var totalCount = results.Count;
            
            Console.WriteLine($"\n🎯 ОБЩИЙ РЕЗУЛЬТАТ: {successCount}/{totalCount} тестов прошли успешно");
            
            if (successCount == totalCount)
            {
                Console.WriteLine("🎉 ВСЕ КОМПОНЕНТЫ OBIZ РАБОТАЮТ КОРРЕКТНО!");
            }
            else
            {
                Console.WriteLine("⚠️ Обнаружены проблемы в некоторых компонентах");
            }
        }

        public void Dispose()
        {
            _restClient?.Dispose();
            _socketClient?.Dispose();
        }
    }
}
