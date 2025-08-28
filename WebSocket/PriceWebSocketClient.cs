using System;
using System.Threading.Tasks;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Objects.Models.Spot.Socket;

namespace WebSocket
{
    public class PriceWebSocketClient
    {
        private readonly BinanceSocketClient _socketClient;
        private readonly string _symbol;
        private decimal _currentPrice;
        private bool _isConnected = false;

        public event Action<decimal>? OnPriceUpdate;

        public PriceWebSocketClient(BinanceSocketClient socketClient, string symbol)
        {
            _socketClient = socketClient;
            _symbol = symbol;
        }

        public async Task ConnectAsync()
        {
            if (_isConnected) return;

            var subscription = await _socketClient.UsdFuturesApi.SubscribeToTickerUpdatesAsync(
                _symbol,
                (update) =>
                {
                    _currentPrice = update.Data.LastPrice;
                    OnPriceUpdate?.Invoke(_currentPrice);
                });

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

        public decimal GetCurrentPrice()
        {
            return _currentPrice;
        }

        public bool IsConnected()
        {
            return _isConnected;
        }
    }
}