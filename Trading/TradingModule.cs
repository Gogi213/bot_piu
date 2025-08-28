using System;
using System.Threading.Tasks;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Objects;
using Binance.Net.Enums;
using Config;
using WebSocket;

namespace Trading
{
    public class TradingModule
    {
        private readonly BinanceRestClient _restClient;
        private readonly BinanceSocketClient _socketClient;
        private readonly TradingConfig _config;
        private PriceWebSocketClient? _priceWebSocket;
        private OrderWebSocketClient? _orderWebSocket;
        private decimal _currentPrice;

        public TradingModule(BinanceRestClient restClient, BinanceSocketClient socketClient, TradingConfig config)
        {
            _restClient = restClient;
            _socketClient = socketClient;
            _config = config;
        }

        public async Task InitializeWebSocketAsync()
        {
            // Получаем listen key для ордеров
            var listenKeyResponse = await _restClient.UsdFuturesApi.Account.StartUserStreamAsync();
            if (!listenKeyResponse.Success)
            {
                throw new Exception($"Ошибка получения listen key: {listenKeyResponse.Error}");
            }

            var listenKey = listenKeyResponse.Data;

            // Создаем WebSocket клиенты
            _priceWebSocket = new PriceWebSocketClient(_socketClient, _config.Symbol);
            _orderWebSocket = new OrderWebSocketClient(_socketClient, listenKey);

            // Подписываемся на обновления цены
            _priceWebSocket.OnPriceUpdate += (price) =>
            {
                _currentPrice = price;
            };

            // Подписываемся на обновления ордеров
            _orderWebSocket.OnOrderUpdate += (orderData) =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📡 WebSocket ордер обновление: {orderData.OrderId}, статус: {orderData.Status}");
            };

            // Подключаемся
            await _priceWebSocket.ConnectAsync();
            await _orderWebSocket.ConnectAsync();

            // Ждем немного для инициализации
            await Task.Delay(1000);
        }

        public async Task<decimal> GetCurrentPriceAsync()
        {
            if (_priceWebSocket != null && _priceWebSocket.IsConnected())
            {
                if (_currentPrice > 0)
                    return _currentPrice;
            }

            // Fallback to REST API if WebSocket not available or price is 0
            var priceResponse = await _restClient.UsdFuturesApi.ExchangeData.GetPriceAsync(_config.Symbol);
            if (!priceResponse.Success)
            {
                throw new Exception($"Ошибка получения цены: {priceResponse.Error}");
            }
            if (priceResponse.Data.Price <= 0)
            {
                throw new Exception($"Неверная цена для {_config.Symbol}: {priceResponse.Data.Price}");
            }
            return priceResponse.Data.Price;
        }

        public async Task<(decimal quantity, decimal takePrice, decimal stopPrice, decimal tickSize)> CalculateOrderParametersAsync(decimal currentPrice)
        {
            if (currentPrice <= 0)
            {
                throw new ArgumentException($"Неверная текущая цена: {currentPrice}");
            }
            
            var quantity = Math.Floor(_config.UsdAmount / currentPrice);
            var tickSize = _config.TickSize;
            
            bool isBuy = _config.Side.ToUpper() == "BUY";
            
            decimal takePrice, stopPrice;
            
            if (isBuy)
            {
                // BUY: тейк выше, стоп ниже
                takePrice = Math.Round(currentPrice * (1 + _config.TakeProfitPercent / 100) / tickSize) * tickSize;
                stopPrice = Math.Round(currentPrice * (1 - _config.StopLossPercent / 100) / tickSize) * tickSize;
                
                // Дополнительная проверка для BUY
                if (stopPrice >= currentPrice)
                {
                    stopPrice = Math.Round(currentPrice * (1 - _config.StopLossPercent / 100 * 2) / tickSize) * tickSize;
                }
            }
            else
            {
                // SELL: тейк ниже, стоп выше
                takePrice = Math.Round(currentPrice * (1 - _config.TakeProfitPercent / 100) / tickSize) * tickSize;
                stopPrice = Math.Round(currentPrice * (1 + _config.StopLossPercent / 100) / tickSize) * tickSize;
                
                // Дополнительная проверка для SELL
                if (stopPrice <= currentPrice)
                {
                    stopPrice = Math.Round(currentPrice * (1 + _config.StopLossPercent / 100 * 2) / tickSize) * tickSize;
                }
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 Параметры {_config.Side}: цена={currentPrice:F6}, тейк={takePrice:F6}, стоп={stopPrice:F6}");
            
            return (quantity, takePrice, stopPrice, tickSize);
        }

        public async Task<dynamic> PlaceEntryOrderAsync(decimal quantity)
        {
            var side = _config.Side.ToUpper() == "SELL" ? Binance.Net.Enums.OrderSide.Sell : Binance.Net.Enums.OrderSide.Buy;
            var entryOrder = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: _config.Symbol,
                side: side,
                type: Binance.Net.Enums.FuturesOrderType.Market,
                quantity: quantity
            );

            if (!entryOrder.Success)
            {
                throw new Exception($"Ошибка входного ордера: {entryOrder.Error}");
            }

            return entryOrder.Data;
        }

        public async Task<dynamic> PlaceTakeProfitOrderAsync(decimal quantity, decimal takePrice)
        {
            var side = _config.Side.ToUpper() == "SELL" ? Binance.Net.Enums.OrderSide.Buy : Binance.Net.Enums.OrderSide.Sell;
            var takeOrder = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: _config.Symbol,
                side: side,
                type: Binance.Net.Enums.FuturesOrderType.TakeProfitMarket,
                quantity: quantity,
                stopPrice: takePrice
            );

            if (!takeOrder.Success)
            {
                throw new Exception($"Ошибка take ордера: {takeOrder.Error}");
            }

            return takeOrder.Data;
        }

        public async Task<dynamic> PlaceStopLossOrderAsync(decimal quantity, decimal stopPrice)
        {
            var side = _config.Side.ToUpper() == "SELL" ? Binance.Net.Enums.OrderSide.Buy : Binance.Net.Enums.OrderSide.Sell;
            var stopOrder = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol: _config.Symbol,
                side: side,
                type: Binance.Net.Enums.FuturesOrderType.StopMarket,
                quantity: quantity,
                stopPrice: stopPrice
            );

            if (!stopOrder.Success)
            {
                throw new Exception($"Ошибка stop ордера: {stopOrder.Error}");
            }

            return stopOrder.Data;
        }

        public async Task MonitorOrdersAsync(dynamic takeOrder, dynamic stopOrder, decimal entryPrice)
        {
            bool breakEvenActivated = false;
            bool isBuy = _config.Side.ToUpper() == "BUY";

            while (true)
            {
                try
                {
                    decimal currentPrice = 0;
                    if (_config.EnableBreakEven && !breakEvenActivated)
                    {
                        currentPrice = await GetCurrentPriceAsync();
                    }

                    // Параллельная проверка статуса ордеров
                    var takeStatusTask = _restClient.UsdFuturesApi.Trading.GetOrderAsync(_config.Symbol, takeOrder.Id);
                    var stopStatusTask = _restClient.UsdFuturesApi.Trading.GetOrderAsync(_config.Symbol, stopOrder.Id);

                    await Task.WhenAll(takeStatusTask, stopStatusTask);

                    var takeStatus = takeStatusTask.Result;
                    var stopStatus = stopStatusTask.Result;

                    // Активация безубытка
                    if (_config.EnableBreakEven && !breakEvenActivated && currentPrice > 0)
                    {
                        bool shouldActivateBreakEven = false;
                        
                        if (isBuy)
                        {
                            // BUY: активируем когда цена выросла на нужный процент
                            var breakEvenActivationPrice = entryPrice * (1 + _config.BreakEvenActivationPercent / 100);
                            shouldActivateBreakEven = currentPrice >= breakEvenActivationPrice;
                        }
                        else
                        {
                            // SELL: активируем когда цена упала на нужный процент
                            var breakEvenActivationPrice = entryPrice * (1 - _config.BreakEvenActivationPercent / 100);
                            shouldActivateBreakEven = currentPrice <= breakEvenActivationPrice;
                        }

                        if (shouldActivateBreakEven)
                        {
                            var breakEvenStartTime = DateTime.Now;
                            var triggerPercent = Math.Abs((currentPrice - entryPrice) / entryPrice) * 100;
                            Console.WriteLine($"[{breakEvenStartTime:HH:mm:ss.fff}] ⚡ ТРИГГЕР БЕЗУБЫТКА АКТИВИРОВАН! (цена: {currentPrice:F6}, {triggerPercent:F2}%)");

                            // Рассчитываем новый стоп для безубытка
                            decimal breakEvenStopPrice;
                            if (isBuy)
                            {
                                // BUY: безубыток стоп
                                if (_config.BreakEvenStopLossPercent == 0)
                                {
                                    breakEvenStopPrice = entryPrice; // Точно на входе
                                }
                                else
                                {
                                    breakEvenStopPrice = Math.Round(entryPrice * (1 + _config.BreakEvenStopLossPercent / 100) / _config.TickSize) * _config.TickSize;
                                }
                            }
                            else
                            {
                                // SELL: безубыток стоп
                                if (_config.BreakEvenStopLossPercent == 0)
                                {
                                    breakEvenStopPrice = entryPrice; // Точно на входе
                                }
                                else
                                {
                                    breakEvenStopPrice = Math.Round(entryPrice * (1 - _config.BreakEvenStopLossPercent / 100) / _config.TickSize) * _config.TickSize;
                                }
                            }

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔄 Перестановка безубытка: {breakEvenStopPrice:F6} (было {_config.BreakEvenStopLossPercent}% от {entryPrice:F6})");

                            // БЫСТРАЯ отмена и размещение
                            var cancelStart = DateTime.Now;
                            var cancelTask = await _restClient.UsdFuturesApi.Trading.CancelOrderAsync(_config.Symbol, stopOrder.Id);
                            var cancelTime = DateTime.Now - cancelStart;

                            if (cancelTask.Success)
                            {
                                var placeStart = DateTime.Now;
                                var newStopOrderTask = await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                                    symbol: _config.Symbol,
                                    side: isBuy ? Binance.Net.Enums.OrderSide.Sell : Binance.Net.Enums.OrderSide.Buy,
                                    type: Binance.Net.Enums.FuturesOrderType.StopMarket,
                                    quantity: stopOrder.Quantity,
                                    stopPrice: breakEvenStopPrice
                                );
                                var placeTime = DateTime.Now - placeStart;
                                var totalTime = DateTime.Now - cancelStart;

                                if (newStopOrderTask.Success)
                                {
                                    stopOrder = newStopOrderTask.Data;
                                    breakEvenActivated = true;
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ БЕЗУБЫТОК АКТИВИРОВАН (стоп: {breakEvenStopPrice:F6}, ID: {stopOrder.Id}) [отмена: {cancelTime.TotalMilliseconds:F0}ms, постановка: {placeTime.TotalMilliseconds:F0}ms, всего: {totalTime.TotalMilliseconds:F0}ms]");
                                }
                                else
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка активации безубытка: {newStopOrderTask.Error}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка отмены ордера для безубытка: {cancelTask.Error}");
                            }
                        }
                    }

                    // Если take ордер исполнен - отменяем stop
                    if (takeStatus.Success && takeStatus.Data.Status == Binance.Net.Enums.OrderStatus.Filled)
                    {
                        var exitPrice = await GetCurrentPriceAsync();
                        var profitPercent = Math.Abs((exitPrice - entryPrice) / entryPrice) * 100;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 Take-профит исполнен! Отменяю стоп-лосс... (цена: {exitPrice:F6}, +{profitPercent:F2}%)");
                        await _restClient.UsdFuturesApi.Trading.CancelOrderAsync(_config.Symbol, stopOrder.Id);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Стоп-лосс отменен. Позиция закрыта в прибыль! (цена: {exitPrice:F6}, +{profitPercent:F2}%)");
                        break;
                    }

                    // Если stop ордер исполнен - отменяем take
                    if (stopStatus.Success && stopStatus.Data.Status == Binance.Net.Enums.OrderStatus.Filled)
                    {
                        var exitPrice = await GetCurrentPriceAsync();
                        var lossPercent = Math.Abs((exitPrice - entryPrice) / entryPrice) * 100;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Стоп-лосс исполнен! Отменяю тейк-профит... (цена: {exitPrice:F6}, -{lossPercent:F2}%)");
                        await _restClient.UsdFuturesApi.Trading.CancelOrderAsync(_config.Symbol, takeOrder.Id);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Тейк-профит отменен. Позиция закрыта в убыток! (цена: {exitPrice:F6}, -{lossPercent:F2}%)");
                        break;
                    }

                    await Task.Delay((int)(_config.MonitorIntervalSeconds * 1000));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка мониторинга: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }

        public async Task ExecuteTradeAsync()
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 Запуск торговли: {_config.Side} {_config.Symbol}");
                
                // Инициализируем WebSocket для получения realtime данных
                await InitializeWebSocketAsync();

                var currentPrice = await GetCurrentPriceAsync();

                var (quantity, takePrice, stopPrice, tickSize) = await CalculateOrderParametersAsync(currentPrice);

                // Размещаем входной ордер
                var entryOrder = await PlaceEntryOrderAsync(quantity);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Входной ордер {_config.Side} размещен: {entryOrder.Id} (цена: {currentPrice:F6})");

                // Цена входа для безубытка
                var entryPrice = currentPrice;

                // Размещаем TAKE PROFIT ордер
                var takeOrder = await PlaceTakeProfitOrderAsync(quantity, takePrice);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Take PROFIT ордер размещен: {takeOrder.Id} (уровень: {takePrice:F6})");

                // Размещаем STOP LOSS ордер
                var stopOrder = await PlaceStopLossOrderAsync(quantity, stopPrice);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Stop LOSS ордер размещен: {stopOrder.Id} (уровень: {stopPrice:F6})");

                if (_config.EnableBreakEven)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚙️ Безубыток включен: активация при {_config.BreakEvenActivationPercent}%, стоп при {_config.BreakEvenStopLossPercent}%");
                }

                // Мониторинг ордеров с WebSocket
                await MonitorOrdersAsync(takeOrder, stopOrder, entryPrice);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка: {ex.Message}");
            }
            finally
            {
                // Отключаем WebSocket при завершении
                if (_priceWebSocket != null)
                    await _priceWebSocket.DisconnectAsync();
                if (_orderWebSocket != null)
                    await _orderWebSocket.DisconnectAsync();
            }
        }
    }
}