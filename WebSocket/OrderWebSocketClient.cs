using System;
using System.Threading.Tasks;
using Binance.Net.Clients;
using CryptoExchange.Net.Sockets;

namespace WebSocket
{
    /// <summary>
    /// Простой WebSocket клиент для мониторинга ордеров
    /// </summary>
    public class OrderWebSocketClient : IDisposable
    {
        private readonly BinanceSocketClient _socketClient;
        private readonly BinanceRestClient _restClient;
        private UpdateSubscription? _subscription;
        private string _listenKey;

        public event Action<dynamic>? OnOrderUpdate;
        public event Action<string>? OnError;

        public OrderWebSocketClient(BinanceSocketClient socketClient, BinanceRestClient restClient, string initialListenKey)
        {
            _socketClient = socketClient;
            _restClient = restClient;
            _listenKey = initialListenKey;
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔌 Подключение к user stream...");

                var subscription = await _socketClient.UsdFuturesApi.SubscribeToUserDataUpdatesAsync(
                    _listenKey,
                    (update) => // onOrderUpdate
                    {
                        try
                        {
                            if (update?.Data != null)
                            {
                                OnOrderUpdate?.Invoke(update.Data);
                            }
                        }
                        catch (Exception ex)
                        {
                            OnError?.Invoke($"Order update error: {ex.Message}");
                        }
                    },
                    null, // onAccountUpdate
                    null, // onConfigUpdate
                    null, // onMarginUpdate
                    (expired) => // onListenKeyExpired
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Listen key истек, переподключение...");
                        Task.Run(async () => await RefreshListenKeyAndReconnect());
                    },
                    null, // onStrategyUpdate
                    null  // onGridUpdate
                );

                if (subscription.Success)
                {
                    _subscription = subscription.Data;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ Подключено к user stream");
                    return true;
                }
                else
                {
                    OnError?.Invoke($"User stream subscription failed: {subscription.Error?.Message ?? "Unknown error"}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Connection exception: {ex.Message}");
                return false;
            }
        }

        private async Task RefreshListenKeyAndReconnect()
        {
            try
            {
                var newKeyResponse = await _restClient.UsdFuturesApi.Account.StartUserStreamAsync();
                if (newKeyResponse.Success)
                {
                    _listenKey = newKeyResponse.Data;
                    await DisconnectAsync();
                    await ConnectAsync();
                }
                else
                {
                    OnError?.Invoke($"Failed to refresh listen key: {newKeyResponse.Error}");
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Listen key refresh error: {ex.Message}");
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
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ Ошибка отключения user stream: {ex.Message}");
            }
        }

        public bool IsConnected() => _subscription != null;

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