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

            var tickSize = 0.0001m; // фиксированный tickSize для тестирования

            var takePrice = Math.Round(currentPrice * (1 + _config.TakeProfitPercent / 100) / tickSize) * tickSize;
            var stopPrice = Math.Round(currentPrice * (1 - _config.StopLossPercent / 100) / tickSize) * tickSize;

            // Для BUY ордера стоп-лосс должен быть ниже текущей цены
            if (_config.Side.ToUpper() == "BUY" && stopPrice >= currentPrice)
            {
                stopPrice = Math.Round(currentPrice * (1 - _config.StopLossPercent / 100 * 2) / tickSize) * tickSize;
            }

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

            while (true)
            {
                try
                {
                    // Оптимизация: получаем цену только если безубыток не активирован
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
                        var breakEvenActivationPrice = entryPrice * (1 + (_config.Side.ToUpper() == "SELL" ? -1 : 1) * _config.BreakEvenActivationPercent / 100);
                        var breakEvenStopPrice = Math.Round(entryPrice * (1 + (_config.Side.ToUpper() == "SELL" ? 1 : -1) * _config.BreakEvenStopLossPercent / 100) / 0.0001m) * 0.0001m;

                        if (currentPrice >= breakEvenActivationPrice)
                        {
                            var breakEvenStartTime = DateTime.Now;
                            var triggerPercent = ((currentPrice - entryPrice) / entryPrice) * 100;
                            Console.WriteLine($"[{breakEvenStartTime:HH:mm:ss.fff}] ⚡ ТРИГГЕР БЕЗУБЫТКА АКТИВИРОВАН! (цена: {currentPrice:F6}, +{triggerPercent:F2}%)");

                            // Параллельная отмена и размещение
                            var cancelTask = _restClient.UsdFuturesApi.Trading.CancelOrderAsync(_config.Symbol, stopOrder.Id);
                            var newStopOrderTask = _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                                symbol: _config.Symbol,
                                side: _config.Side.ToUpper() == "SELL" ? Binance.Net.Enums.OrderSide.Buy : Binance.Net.Enums.OrderSide.Sell,
                                type: Binance.Net.Enums.FuturesOrderType.StopMarket,
                                quantity: stopOrder.Quantity,
                                stopPrice: breakEvenStopPrice
                            );

                            await Task.WhenAll(cancelTask, newStopOrderTask);

                            if (newStopOrderTask.Result.Success)
                            {
                                stopOrder = newStopOrderTask.Result.Data;
                                breakEvenActivated = true;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ БЕЗУБЫТОК АКТИВИРОВАН (новый стоп: {breakEvenStopPrice:F6}, ID: {stopOrder.Id})");
                            }
                            else
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка активации безубытка: {newStopOrderTask.Result.Error}");
                            }
                            break; // Выходим из цикла проверки, чтобы не повторять
                        }
                    }

                    // Если take ордер исполнен - отменяем stop
                    if (takeStatus.Success && takeStatus.Data.Status == Binance.Net.Enums.OrderStatus.Filled)
                    {
                        var exitPrice = await GetCurrentPriceAsync();
                        var profitPercent = ((exitPrice - entryPrice) / entryPrice) * 100;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 Take-профит исполнен! Отменяю стоп-лосс... (цена: {exitPrice:F6}, +{profitPercent:F2}%)");
                        await _restClient.UsdFuturesApi.Trading.CancelOrderAsync(_config.Symbol, stopOrder.Id);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Стоп-лосс отменен. Позиция закрыта в прибыль! (цена: {exitPrice:F6}, +{profitPercent:F2}%)");
                        break;
                    }

                    // Если stop ордер исполнен - отменяем take
                    if (stopStatus.Success && stopStatus.Data.Status == Binance.Net.Enums.OrderStatus.Filled)
                    {
                        var exitPrice = await GetCurrentPriceAsync();
                        var lossPercent = ((exitPrice - entryPrice) / entryPrice) * 100;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Стоп-лосс исполнен! Отменяю тейк-профит... (цена: {exitPrice:F6}, {lossPercent:F2}%)");
                        await _restClient.UsdFuturesApi.Trading.CancelOrderAsync(_config.Symbol, takeOrder.Id);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Тейк-профит отменен. Позиция закрыта в убыток! (цена: {exitPrice:F6}, {lossPercent:F2}%)");
                        break;
                    }

                    // Уменьшаем интервал до 10ms для быстрой реакции
                    await Task.Delay((int)(_config.MonitorIntervalSeconds * 1000));
                }
                catch (Exception)
                {
                    // Ошибка мониторинга
                    await Task.Delay(1000);
                }
            }
        }

        public async Task ExecuteTradeAsync()
        {
            try
            {
                // Инициализируем WebSocket для получения realtime данных
                await InitializeWebSocketAsync();

                var currentPrice = await GetCurrentPriceAsync();

                var (quantity, takePrice, stopPrice, tickSize) = await CalculateOrderParametersAsync(currentPrice);

                // Размещаем входной ордер
                var entryOrder = await PlaceEntryOrderAsync(quantity);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Входной ордер {_config.Side} размещен: {entryOrder.Id} (цена: {currentPrice:F6})");

                // Цена входа для безубытка
                var entryPrice = currentPrice;
                var breakEvenActivationPrice = entryPrice * (1 + _config.BreakEvenActivationPercent / 100);
                var breakEvenStopPrice = Math.Round(entryPrice * (1 - _config.BreakEvenStopLossPercent / 100) / _config.TickSize) * _config.TickSize;

                // Размещаем TAKE PROFIT ордер
                var takeOrder = await PlaceTakeProfitOrderAsync(quantity, takePrice);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Take PROFIT ордер размещен: {takeOrder.Id} (уровень: {takePrice:F6})");

                // Размещаем STOP LOSS ордер
                var stopOrder = await PlaceStopLossOrderAsync(quantity, stopPrice);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Stop LOSS ордер размещен: {stopOrder.Id} (уровень: {stopPrice:F6})");

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