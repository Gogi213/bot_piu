using System;
using System.Threading.Tasks;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Objects.Models.Futures.Socket;

namespace WebSocket
{
    public class OrderWebSocketClient
    {
        private readonly BinanceSocketClient _socketClient;
        private readonly string _listenKey;
        private bool _isConnected = false;

        public event Action<dynamic>? OnOrderUpdate;

        public OrderWebSocketClient(BinanceSocketClient socketClient, string listenKey)
        {
            _socketClient = socketClient;
            _listenKey = listenKey;
        }

        public async Task ConnectAsync()
        {
            if (_isConnected) return;

            var subscription = await _socketClient.UsdFuturesApi.SubscribeToUserDataUpdatesAsync(
                _listenKey,
                null, // onMarginUpdate
                null, // onAccountUpdate
                (update) =>
                {
                    // Обработка различных типов обновлений с помощью dynamic
                    if (update?.Data != null)
                    {
                        dynamic data = update.Data;
                        if (data.GetType()?.Name?.Contains("OrderUpdate") == true)
                        {
                            OnOrderUpdate?.Invoke(data);
                        }
                    }
                },
                null, // onBalanceUpdate
                null, // onStrategyUpdate
                null, // onGridUpdate
                default  // cancellationToken
            );

            if (subscription.Success)
            {
                _isConnected = true;
            }
        }

        public async Task DisconnectAsync()
        {
            if (!_isConnected) return;

            await _socketClient.UnsubscribeAllAsync();
            _isConnected = false;
        }

        public bool IsConnected()
        {
            return _isConnected;
        }
    }
}