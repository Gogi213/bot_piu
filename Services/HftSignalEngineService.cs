using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using Models;
using Config;

namespace Services
{
    /// <summary>
    /// Псевдо-HFT движок для анализа торговых сигналов с минимальными задержками
    /// </summary>
    public class HftSignalEngineService
    {
        private readonly TradingStrategyService _strategyService;
        private readonly DataStorageService _dataStorage;
        private readonly BackendConfig _config;
        
        // Высокопроизводительные структуры данных
        private readonly ConcurrentDictionary<string, HftSignalState> _signalStates = new();
        private readonly ConcurrentDictionary<string, PriceBuffer> _priceBuffers = new();
        private readonly ConcurrentQueue<HftSignalEvent> _signalEvents = new();
        
        // Высокочастотные таймеры
        private Timer? _ultraFastTimer;  // 100ms - ультрабыстрый анализ
        private Timer? _fastTimer;       // 1000ms - быстрый анализ
        private Timer? _cleanupTimer;    // 5000ms - очистка буферов
        
        private volatile bool _isRunning = false;
        private readonly object _lockObject = new object();
        
        // Статистика производительности
        private long _totalAnalyses = 0;
        private long _totalSignalChanges = 0;
        private long _totalProcessingTimeMs = 0;
        private DateTime _startTime;
        
        // События для HFT уведомлений
        public event Action<HftSignalEvent>? OnHftSignalChange;
        public event Action<HftPerformanceStats>? OnPerformanceUpdate;

        public HftSignalEngineService(
            TradingStrategyService strategyService,
            DataStorageService dataStorage,
            BackendConfig config)
        {
            _strategyService = strategyService;
            _dataStorage = dataStorage;
            _config = config;
        }

        /// <summary>
        /// Запуск HFT движка
        /// </summary>
        public async Task<bool> StartAsync()
        {
            if (_isRunning)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚠️ HFT движок уже запущен");
                return false;
            }

            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚡ ЗАПУСК HFT SIGNAL ENGINE");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ================================");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 Режим: Псевдо-HFT (High Frequency Trading)");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⏱️ Ультрабыстрый анализ: каждые 200мс");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 Быстрый анализ: каждые 1500мс");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🧹 Очистка буферов: каждые 5000мс");
                
                _isRunning = true;
                _startTime = DateTime.UtcNow;
                _totalAnalyses = 0;
                _totalSignalChanges = 0;
                _totalProcessingTimeMs = 0;

                // Инициализация состояний для активных монет
                await InitializeHftStatesAsync();

                // Запуск высокочастотных таймеров (менее агрессивно для снижения лагов)
                _ultraFastTimer = new Timer(async _ => await UltraFastAnalysisAsync(), null, 200, 200);
                _fastTimer = new Timer(async _ => await FastAnalysisAsync(), null, 1500, 1500);
                _cleanupTimer = new Timer(_ => CleanupBuffers(), null, 5000, 5000);

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ HFT движок запущен успешно");
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка запуска HFT движка: {ex.Message}");
                _isRunning = false;
                return false;
            }
        }

        /// <summary>
        /// Обновление цены в реальном времени (вызывается WebSocket)
        /// </summary>
        public void UpdatePrice(string symbol, decimal price)
        {
            if (!_isRunning) return;

            var timestamp = DateTime.UtcNow;
            
            // Добавляем в буфер цен
            if (!_priceBuffers.TryGetValue(symbol, out var buffer))
            {
                buffer = new PriceBuffer();
                _priceBuffers[symbol] = buffer;
            }
            
            buffer.AddPrice(price, timestamp);
            
            // Обновляем состояние сигнала если есть значительное изменение цены
            if (_signalStates.TryGetValue(symbol, out var state))
            {
                var priceChangePercent = Math.Abs((price - state.LastPrice) / state.LastPrice * 100);
                if (priceChangePercent >= 0.1m) // 0.1% изменение
                {
                    state.LastPrice = price;
                    state.LastPriceUpdate = timestamp;
                    state.PriceChangesCount++;
                    
                    // Помечаем для быстрого анализа
                    state.NeedsAnalysis = true;
                }
            }
        }

        /// <summary>
        /// Ультрабыстрый анализ (каждые 100мс) - только приоритетные монеты
        /// </summary>
        private async Task UltraFastAnalysisAsync()
        {
            if (!_isRunning) return;

            var sw = Stopwatch.StartNew();
            var analysisCount = 0;

            try
            {
                // Анализируем только монеты с изменениями или высокой волатильностью
                var prioritySymbols = _signalStates
                    .Where(kvp => kvp.Value.NeedsAnalysis || kvp.Value.IsHighVolatility)
                    .Take(5) // Ограничиваем для скорости
                    .ToList();

                foreach (var kvp in prioritySymbols)
                {
                    var symbol = kvp.Key;
                    var state = kvp.Value;
                    
                    var coinData = _dataStorage.GetCoinData(symbol);
                    if (coinData == null) continue;

                    // Быстрый анализ сигнала
                    var newSignal = _strategyService.AnalyzeCoin(coinData);
                    analysisCount++;

                    // Проверяем изменения
                    if (state.CurrentSignal != newSignal.FinalSignal)
                    {
                        var oldSignal = state.CurrentSignal;
                        state.CurrentSignal = newSignal.FinalSignal;
                        state.LastSignalChange = DateTime.UtcNow;
                        state.SignalChangesCount++;
                        
                        Interlocked.Increment(ref _totalSignalChanges);

                        // Создаем HFT событие
                        var hftEvent = new HftSignalEvent
                        {
                            Symbol = symbol,
                            OldSignal = oldSignal,
                            NewSignal = newSignal.FinalSignal,
                            ZScore = newSignal.ZScore,
                            Price = newSignal.CurrentPrice,
                            Timestamp = DateTime.UtcNow,
                            LatencyMs = sw.ElapsedMilliseconds,
                            EventType = HftEventType.UltraFast
                        };

                        _signalEvents.Enqueue(hftEvent);
                        
                        // Оставляем только сигналы BUY/SELL
                        if (newSignal.FinalSignal != "FLAT")
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⚡ СИГНАЛ: {symbol} {oldSignal}→{GetSignalEmoji(newSignal.FinalSignal)}{newSignal.FinalSignal} " +
                                             $"Z={newSignal.ZScore:F2} P={newSignal.CurrentPrice:F6}");
                        }

                        OnHftSignalChange?.Invoke(hftEvent);
                    }

                    state.NeedsAnalysis = false;
                    state.LastAnalysis = DateTime.UtcNow;
                }

                Interlocked.Add(ref _totalAnalyses, analysisCount);
                Interlocked.Add(ref _totalProcessingTimeMs, sw.ElapsedMilliseconds);

                // Статистика каждые 10 секунд
                if (_totalAnalyses % 100 == 0 && _totalAnalyses > 0)
                {
                    var stats = GetPerformanceStats();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 HFT-STATS: {stats.AnalysesPerSecond:F0}/сек, " +
                                     $"avg={stats.AverageLatencyMs:F1}ms, сигналов={stats.TotalSignalChanges}");
                    
                    OnPerformanceUpdate?.Invoke(stats);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ HFT Ultra Fast Error: {ex.Message}");
            }
            finally
            {
                sw.Stop();
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Быстрый анализ (каждую секунду) - все активные монеты
        /// </summary>
        private Task FastAnalysisAsync()
        {
            if (!_isRunning) return Task.CompletedTask;

            var sw = Stopwatch.StartNew();

            try
            {
                var activeCoins = _dataStorage.GetFilteredCoins(_config.MinVolumeUsdt, _config.MinNatrPercent);
                var analysisCount = 0;

                // Параллельный анализ для скорости
                var tasks = activeCoins.Take(20).Select(coin =>
                {
                    try
                    {
                        var signal = _strategyService.AnalyzeCoin(coin);
                        Interlocked.Increment(ref analysisCount);

                        // Обновляем состояние
                        var state = _signalStates.GetOrAdd(coin.Symbol, _ => new HftSignalState 
                        { 
                            Symbol = coin.Symbol,
                            CurrentSignal = "FLAT",
                            IsHighVolatility = (coin.Natr ?? 0) >= _config.MinNatrPercent * 2
                        });

                        if (state.CurrentSignal != signal.FinalSignal)
                        {
                            var oldSignal = state.CurrentSignal;
                            state.CurrentSignal = signal.FinalSignal;
                            state.LastSignalChange = DateTime.UtcNow;
                            state.SignalChangesCount++;
                            
                            Interlocked.Increment(ref _totalSignalChanges);

                            var hftEvent = new HftSignalEvent
                            {
                                Symbol = coin.Symbol,
                                OldSignal = oldSignal,
                                NewSignal = signal.FinalSignal,
                                ZScore = signal.ZScore,
                                Price = signal.CurrentPrice,
                                Timestamp = DateTime.UtcNow,
                                LatencyMs = sw.ElapsedMilliseconds,
                                EventType = HftEventType.Fast
                            };

                            _signalEvents.Enqueue(hftEvent);

                            // Оставляем только сигналы BUY/SELL  
                            if (signal.FinalSignal != "FLAT")
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🚀 СИГНАЛ: {coin.Symbol} {oldSignal}→{GetSignalEmoji(signal.FinalSignal)}{signal.FinalSignal} " +
                                                 $"Z={signal.ZScore:F2} NATR={coin.Natr:F2}%");
                            }

                            OnHftSignalChange?.Invoke(hftEvent);
                        }

                        state.LastAnalysis = DateTime.UtcNow;
                        return signal;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ HFT Fast Error for {coin.Symbol}: {ex.Message}");
                        return null;
                    }
                });

                var results = tasks.ToList();
                
                Interlocked.Add(ref _totalAnalyses, analysisCount);
                Interlocked.Add(ref _totalProcessingTimeMs, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ HFT Fast Analysis Error: {ex.Message}");
            }
            finally
            {
                sw.Stop();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Инициализация HFT состояний
        /// </summary>
        private Task InitializeHftStatesAsync()
        {
            var activeCoins = _dataStorage.GetFilteredCoins(_config.MinVolumeUsdt, _config.MinNatrPercent);
            
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🎯 Инициализация HFT состояний для {activeCoins.Count} монет...");

            foreach (var coin in activeCoins)
            {
                var signal = _strategyService.AnalyzeCoin(coin);
                
                var state = new HftSignalState
                {
                    Symbol = coin.Symbol,
                    CurrentSignal = signal.FinalSignal,
                    LastPrice = coin.CurrentPrice,
                    LastAnalysis = DateTime.UtcNow,
                    LastPriceUpdate = DateTime.UtcNow,
                    IsHighVolatility = (coin.Natr ?? 0) >= _config.MinNatrPercent * 1.5m
                };

                _signalStates[coin.Symbol] = state;
                
                // Инициализируем буфер цен
                var buffer = new PriceBuffer();
                buffer.AddPrice(coin.CurrentPrice, DateTime.UtcNow);
                _priceBuffers[coin.Symbol] = buffer;
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ✅ HFT состояния инициализированы");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Очистка буферов для оптимизации памяти
        /// </summary>
        private void CleanupBuffers()
        {
            if (!_isRunning) return;

            try
            {
                var cutoffTime = DateTime.UtcNow.AddMinutes(-5);
                var cleanedBuffers = 0;

                foreach (var buffer in _priceBuffers.Values)
                {
                    var cleaned = buffer.CleanOldPrices(cutoffTime);
                    if (cleaned > 0) cleanedBuffers++;
                }

                // Очищаем старые события
                var eventsToRemove = 0;
                while (_signalEvents.Count > 1000 && _signalEvents.TryDequeue(out _))
                {
                    eventsToRemove++;
                }

                if (cleanedBuffers > 0 || eventsToRemove > 0)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🧹 HFT Cleanup: буферов={cleanedBuffers}, событий={eventsToRemove}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ HFT Cleanup Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Остановка HFT движка
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;

            _isRunning = false;
            
            _ultraFastTimer?.Dispose();
            _fastTimer?.Dispose();
            _cleanupTimer?.Dispose();

            var uptime = DateTime.UtcNow - _startTime;
            var finalStats = GetPerformanceStats();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🛑 HFT движок остановлен");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 ФИНАЛЬНАЯ HFT СТАТИСТИКА:");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    ⏰ Время работы: {uptime.TotalMinutes:F1} мин");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    🔍 Всего анализов: {finalStats.TotalAnalyses:N0}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    🎯 Изменений сигналов: {finalStats.TotalSignalChanges:N0}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    ⚡ Анализов/сек: {finalStats.AnalysesPerSecond:F0}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    📈 Средняя задержка: {finalStats.AverageLatencyMs:F1}мс");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]    🏆 Пиковая производительность: {finalStats.PeakAnalysesPerSecond:F0}/сек");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Получение статистики производительности
        /// </summary>
        public HftPerformanceStats GetPerformanceStats()
        {
            var uptime = DateTime.UtcNow - _startTime;
            var uptimeSeconds = Math.Max(uptime.TotalSeconds, 1);

            return new HftPerformanceStats
            {
                IsRunning = _isRunning,
                Uptime = uptime,
                TotalAnalyses = _totalAnalyses,
                TotalSignalChanges = _totalSignalChanges,
                AnalysesPerSecond = _totalAnalyses / uptimeSeconds,
                AverageLatencyMs = _totalAnalyses > 0 ? (double)_totalProcessingTimeMs / _totalAnalyses : 0,
                ActiveSymbols = _signalStates.Count,
                BufferedPrices = _priceBuffers.Sum(kvp => kvp.Value.Count),
                QueuedEvents = _signalEvents.Count,
                PeakAnalysesPerSecond = Math.Max(_totalAnalyses / uptimeSeconds, 0) // Простая реализация
            };
        }

        private string GetSignalEmoji(string signal) => signal switch
        {
            "LONG" => "🟢",
            "SHORT" => "🔴",
            _ => "⚪"
        };
    }

    // Вспомогательные классы для HFT
    public class HftSignalState
    {
        public string Symbol { get; set; } = string.Empty;
        public string CurrentSignal { get; set; } = "FLAT";
        public decimal LastPrice { get; set; }
        public DateTime LastAnalysis { get; set; }
        public DateTime LastPriceUpdate { get; set; }
        public DateTime LastSignalChange { get; set; }
        public bool NeedsAnalysis { get; set; }
        public bool IsHighVolatility { get; set; }
        public int SignalChangesCount { get; set; }
        public int PriceChangesCount { get; set; }
    }

    public class PriceBuffer
    {
        private readonly ConcurrentQueue<PricePoint> _prices = new();
        private volatile int _count = 0;

        public int Count => _count;

        public void AddPrice(decimal price, DateTime timestamp)
        {
            _prices.Enqueue(new PricePoint { Price = price, Timestamp = timestamp });
            Interlocked.Increment(ref _count);
        }

        public int CleanOldPrices(DateTime cutoffTime)
        {
            var removed = 0;
            while (_prices.TryPeek(out var oldest) && oldest.Timestamp < cutoffTime)
            {
                if (_prices.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _count);
                    removed++;
                }
                else break;
            }
            return removed;
        }
    }

    public class PricePoint
    {
        public decimal Price { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class HftSignalEvent
    {
        public string Symbol { get; set; } = string.Empty;
        public string OldSignal { get; set; } = string.Empty;
        public string NewSignal { get; set; } = string.Empty;
        public decimal ZScore { get; set; }
        public decimal Price { get; set; }
        public DateTime Timestamp { get; set; }
        public long LatencyMs { get; set; }
        public HftEventType EventType { get; set; }
    }

    public enum HftEventType
    {
        UltraFast,  // 100ms анализ
        Fast        // 1000ms анализ
    }

    public class HftPerformanceStats
    {
        public bool IsRunning { get; set; }
        public TimeSpan Uptime { get; set; }
        public long TotalAnalyses { get; set; }
        public long TotalSignalChanges { get; set; }
        public double AnalysesPerSecond { get; set; }
        public double AverageLatencyMs { get; set; }
        public int ActiveSymbols { get; set; }
        public int BufferedPrices { get; set; }
        public int QueuedEvents { get; set; }
        public double PeakAnalysesPerSecond { get; set; }
    }
}
