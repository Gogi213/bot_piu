using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Models;

namespace Services
{
    public class DataStorageService
    {
        // Thread-safe коллекции для хранения данных
        private readonly ConcurrentDictionary<string, CoinData> _universeData = new();
        private readonly ConcurrentDictionary<string, TradingSignal> _lastSignals = new();
        private volatile EngineStatus _engineStatus = new() { IsRunning = false };
        private volatile object? _currentRunner = null;
        private volatile object? _wsEngine = null;
        private volatile object? _lastConfig = null;

        #region Universe Data (пул монет)
        
        public void UpdateCoinData(string symbol, CoinData coinData)
        {
            _universeData.AddOrUpdate(symbol, coinData, (key, existing) => coinData);
        }

        public CoinData? GetCoinData(string symbol)
        {
            return _universeData.TryGetValue(symbol, out var coinData) ? coinData : null;
        }

        public List<CoinData> GetAllCoins()
        {
            return _universeData.Values.ToList();
        }

        public List<CoinData> GetFilteredCoins(decimal minVolume, decimal minNatr)
        {
            return _universeData.Values
                .Where(coin => coin.Volume24h >= minVolume && 
                              coin.Natr.HasValue && 
                              coin.Natr.Value >= minNatr)
                .OrderByDescending(coin => coin.Volume24h)
                .ToList();
        }

        public void ClearUniverseData()
        {
            _universeData.Clear();
        }

        public int GetUniverseCount()
        {
            return _universeData.Count;
        }

        #endregion

        #region Trading Signals
        
        public void UpdateSignal(string symbol, TradingSignal signal)
        {
            _lastSignals.AddOrUpdate(symbol, signal, (key, existing) => signal);
        }

        public TradingSignal? GetSignal(string symbol)
        {
            return _lastSignals.TryGetValue(symbol, out var signal) ? signal : null;
        }

        public List<TradingSignal> GetAllSignals()
        {
            return _lastSignals.Values.ToList();
        }

        public List<TradingSignal> GetActiveSignals()
        {
            return _lastSignals.Values
                .Where(signal => signal.Action != "FLAT")
                .OrderByDescending(signal => signal.Timestamp)
                .ToList();
        }

        public void ClearSignals()
        {
            _lastSignals.Clear();
        }

        public int GetSignalsCount()
        {
            return _lastSignals.Count;
        }

        #endregion

        #region Engine Management

        public void SetEngineStatus(bool isRunning, DateTime? startTime = null)
        {
            _engineStatus.IsRunning = isRunning;
            if (startTime.HasValue)
                _engineStatus.StartTime = startTime;
            
            _engineStatus.ActiveSignals = GetActiveSignals().Count;
            _engineStatus.TotalCoins = GetUniverseCount();
            _engineStatus.LastUpdate = DateTime.UtcNow;
        }

        public EngineStatus GetEngineStatus()
        {
            // Обновляем актуальные счетчики
            _engineStatus.ActiveSignals = GetActiveSignals().Count;
            _engineStatus.TotalCoins = GetUniverseCount();
            _engineStatus.LastUpdate = DateTime.UtcNow;
            
            return _engineStatus;
        }

        public void SetCurrentRunner(object? runner)
        {
            _currentRunner = runner;
        }

        public object? GetCurrentRunner()
        {
            return _currentRunner;
        }

        public void SetWsEngine(object? wsEngine)
        {
            _wsEngine = wsEngine;
        }

        public object? GetWsEngine()
        {
            return _wsEngine;
        }

        public void SetLastConfig(object? config)
        {
            _lastConfig = config;
        }

        public object? GetLastConfig()
        {
            return _lastConfig;
        }

        #endregion

        #region Statistics

        public Dictionary<string, object> GetStatistics()
        {
            var stats = new Dictionary<string, object>
            {
                ["TotalCoins"] = GetUniverseCount(),
                ["ActiveSignals"] = GetActiveSignals().Count,
                ["TotalSignals"] = GetSignalsCount(),
                ["EngineRunning"] = _engineStatus.IsRunning,
                ["LastUpdate"] = _engineStatus.LastUpdate ?? (object)"Never",
                ["StartTime"] = _engineStatus.StartTime ?? (object)"Not started"
            };

            // Статистика по сигналам
            var signals = GetAllSignals();
            if (signals.Any())
            {
                stats["BuySignals"] = signals.Count(s => s.Action == "BUY");
                stats["SellSignals"] = signals.Count(s => s.Action == "SELL");
                stats["FlatSignals"] = signals.Count(s => s.Action == "FLAT");
            }

            // Статистика по волатильности
            var coinsWithNatr = GetAllCoins().Where(c => c.Natr.HasValue).ToList();
            if (coinsWithNatr.Any())
            {
                stats["AvgNatr"] = Math.Round(coinsWithNatr.Average(c => c.Natr!.Value), 4);
                stats["MaxNatr"] = Math.Round(coinsWithNatr.Max(c => c.Natr!.Value), 4);
                stats["MinNatr"] = Math.Round(coinsWithNatr.Min(c => c.Natr!.Value), 4);
            }

            return stats;
        }

        #endregion
    }
}
