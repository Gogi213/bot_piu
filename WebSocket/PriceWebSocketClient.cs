using System;
using System.Threading.Tasks;
using Binance.Net.Clients;
using CryptoExchange.Net.Sockets;

namespace WebSocket
{
    /// <summary>
    /// Простой WebSocket клиент для получения цен
    /// </summary>
    public class PriceWebSocketClient : IDisposable
    {
        private readonly BinanceSocketClient _socketClient;
        private readonly string _symbol;
        private UpdateSubscription? _subscription;
        private decimal _currentPrice;

        public event Action<decimal>? OnPriceUpdate;
        public event Action<string>? OnError;

        public PriceWebSocketClient(BinanceSocketClient socketClient, string symbol)
        {
            _socketClient = socketClient;
            _symbol = symbol;
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔌 Подключение к ценам {_symbol}...");

                var subscription = await _socketClient.UsdFuturesApi.SubscribeToTickerUpdatesAsync(
                    _symbol,
                    (update) =>
                    {
                        try
                        {
                            _currentPrice = update.Data.LastPrice;
                            OnPriceUpdate?.Invoke(_currentPrice);
                        }
                        catch (Exception ex)
                        {
                            OnError?.Invoke($"Price update error: {ex.Message}");
                        }
                    });

                if (subscription.Success)
                {
                    _subscription = subscription.Data;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Подключено к ценам {_symbol}");
                    return true;
                }
                else
                {
                    OnError?.Invoke($"Subscription failed: {subscription.Error?.Message ?? "Unknown error"}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Connection exception: {ex.Message}");
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                if (_subscription != null)
                {
                    await _subscription.CloseAsync();
                    _subscription = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка отключения цен {_symbol}: {ex.Message}");
            }
        }

        public bool IsConnected() => _subscription != null;

        public decimal GetCurrentPrice() => _currentPrice;

        public void Dispose()
        {
            try
            {
                _subscription?.CloseAsync()?.Wait();
            }
            catch
            {
                // Игнорируем ошибки при закрытии
            }
        }
    }
}