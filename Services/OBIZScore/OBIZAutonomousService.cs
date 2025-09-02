using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Services.OBIZScore.Core;
using Services.OBIZScore.Config;
using Models;
using Config;
using Binance.Net.Clients;

namespace Services.OBIZScore
{
    /// <summary>
    /// Полностью автономный сервис OBIZ-Score стратегии
    /// Отдельный модуль, не зависящий от Legacy компонентов
    /// </summary>
    public class OBIZAutonomousService
    {
        private readonly OBIZStrategyConfig _obizConfig;
        private readonly BackendConfig _backendConfig;
        private readonly TradingConfig _tradingConfig;
        private readonly AutoTradingConfig _autoConfig;
        private readonly CoinSelectionConfig _coinSelectionConfig;
        
        // OBIZ компоненты
        private readonly ConcurrentDictionary<string, OBIZScoreStrategy> _strategies;
        private readonly OBIZPositionManager _positionManager;
        private readonly TickDataAdapter _tickAdapter;
        
        // Данные и сервисы
        private readonly DataStorageService _dataStorage;
        private readonly BinanceDataService _binanceService;
        private readonly CoinSelectionService _coinSelectionService;
        
        // WebSocket для реал-тайм данных
        private readonly MultiSymbolWebSocketService _webSocketService;
        
        // Состояние
        private volatile bool _isRunning = false;
        private readonly Timer _analysisTimer;
        private readonly Timer _positionUpdateTimer;
        private List<string> _activeSymbols = new List<string>();
        
        // События
        public event Action<string, OBIZSignal>? OnOBIZSignal;
        public event Action<string, string>? OnPositionOpened;
        public event Action<string, string>? OnPositionClosed;
        public event Action<string>? OnError;

        public OBIZAutonomousService(
            OBIZStrategyConfig obizConfig,
            BackendConfig backendConfig,
            TradingConfig tradingConfig,
            AutoTradingConfig autoConfig,
            CoinSelectionConfig coinSelectionConfig,
            DataStorageService dataStorage,
            BinanceDataService binanceService,
            CoinSelectionService coinSelectionService,
            MultiSymbolWebSocketService webSocketService)
        {
            _obizConfig = obizConfig ?? throw new ArgumentNullException(nameof(obizConfig));
            _backendConfig = backendConfig ?? throw new ArgumentNullException(nameof(backendConfig));
            _tradingConfig = tradingConfig ?? throw new ArgumentNullException(nameof(tradingConfig));
            _autoConfig = autoConfig ?? throw new ArgumentNullException(nameof(autoConfig));
            _coinSelectionConfig = coinSelectionConfig ?? throw new ArgumentNullException(nameof(coinSelectionConfig));
            
            _dataStorage = dataStorage ?? throw new ArgumentNullException(nameof(dataStorage));
            _binanceService = binanceService ?? throw new ArgumentNullException(nameof(binanceService));
            _coinSelectionService = coinSelectionService ?? throw new ArgumentNullException(nameof(coinSelectionService));
            _webSocketService = webSocketService ?? throw new ArgumentNullException(nameof(webSocketService));
            
            // Инициализация OBIZ компонентов
            _strategies = new ConcurrentDictionary<string, OBIZScoreStrategy>();
            _positionManager = new OBIZPositionManager(_obizConfig, _autoConfig, _tradingConfig);
            _tickAdapter = new TickDataAdapter();
            
            // Таймеры для анализа и обновления позиций
            _analysisTimer = new Timer(AnalysisCallback, null, Timeout.Infinite, Timeout.Infinite);
            _positionUpdateTimer = new Timer(PositionUpdateCallback, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Запуск автономного OBIZ сервиса
        /// </summary>
        public async Task<bool> StartAsync()
        {
            if (_isRunning)
            {
                LogWarning("OBIZ service already running");
                return false;
            }

            try
            {
                // Ротация лог файла при запуске
                OBIZJsonLogger.RotateLogFile();
                
                LogInfo("🚀 Starting OBIZ Autonomous Service...");
                LogInfo($"Configuration: {_obizConfig}");

                // 1. Получаем монеты для торговли
                LogInfo("📊 Selecting coins for OBIZ trading...");
                var coinSelection = await _coinSelectionService.GetTradingCoinsAsync();
                if (!coinSelection.Success)
                {
                    LogError($"Failed to select coins: {coinSelection.ErrorMessage}");
                    return false;
                }

                _activeSymbols = coinSelection.SelectedCoins.Select(c => c.Symbol).ToList();
                LogInfo($"✅ Selected {_activeSymbols.Count} coins: {coinSelection.SelectionCriteria}");

                // 2. Инициализируем OBIZ стратегии для каждого символа
                LogInfo("🧠 Initializing OBIZ strategies...");
                foreach (var symbol in _activeSymbols)
                {
                    var strategy = new OBIZScoreStrategy(_obizConfig);
                    _strategies[symbol] = strategy;
                    
                    // Прогреваем стратегию историческими данными
                    await WarmupStrategyAsync(strategy, symbol);
                }
                LogInfo($"✅ Initialized {_strategies.Count} OBIZ strategies");

                // 3. Запускаем WebSocket для реал-тайм данных
                LogInfo("📡 Starting WebSocket for real-time data...");
                await _webSocketService.StartAsync(_activeSymbols);
                
                // Подписываемся на обновления цен
                _webSocketService.OnPriceUpdate += OnPriceUpdateHandler;
                LogInfo("✅ WebSocket started and subscribed to price updates");

                // 4. Запускаем таймеры анализа
                LogInfo("⚡ Starting analysis timers...");
                _analysisTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(500)); // Анализ каждые 500ms
                _positionUpdateTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1)); // Позиции каждую секунду
                
                _isRunning = true;
                LogInfo("🎯 OBIZ Autonomous Service started successfully!");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Failed to start OBIZ service: {ex.Message}");
                OnError?.Invoke(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Остановка сервиса
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;

            LogInfo("🛑 Stopping OBIZ Autonomous Service...");
            
            _isRunning = false;
            
            // Останавливаем таймеры
            _analysisTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _positionUpdateTimer.Change(Timeout.Infinite, Timeout.Infinite);
            
            // Останавливаем WebSocket
            await _webSocketService.StopAsync();
            
            // Закрываем все открытые позиции
            await CloseAllPositionsAsync("Service stopping");
            
            LogInfo("✅ OBIZ Autonomous Service stopped");
        }

        /// <summary>
        /// Прогрев стратегии историческими данными
        /// </summary>
        private async Task WarmupStrategyAsync(OBIZScoreStrategy strategy, string symbol)
        {
            try
            {
                // Получаем исторические свечи
                var candles = await _binanceService.GetHistoricalCandlesAsync(symbol, 100);
                if (candles.Count == 0)
                {
                    LogWarning($"No historical data for {symbol}");
                    return;
                }

                // Конвертируем в тики и прогреваем
                int ticksGenerated = 0;
                foreach (var candle in candles.TakeLast(50)) // Последние 50 свечей
                {
                    var ticks = _tickAdapter.ConvertCandleToTicks(candle, symbol, 3); // 3 тика на свечу
                    foreach (var tick in ticks)
                    {
                        await strategy.ProcessTickAsync(tick, symbol);
                        ticksGenerated++;
                    }
                }

                var stats = strategy.GetCurrentStats();
                LogInfo($"📈 {symbol} warmed up: {ticksGenerated} ticks, Ready: {stats.HasSufficientData}");
            }
            catch (Exception ex)
            {
                LogError($"Error warming up {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработчик обновлений цен от WebSocket
        /// </summary>
        private async void OnPriceUpdateHandler(string symbol, decimal price)
        {
            if (!_isRunning || !_strategies.ContainsKey(symbol)) return;

            try
            {
                // Создаем тик из реал-тайм данных (используем хранимый объем)
                var coinData = _dataStorage.GetCoinData(symbol);
                var volume = coinData?.Volume24h ?? 0m;
                var tick = _tickAdapter.CreateRealTimeTick(symbol, price, volume);
                
                // Анализируем с помощью OBIZ стратегии
                var strategy = _strategies[symbol];
                var decision = await strategy.ProcessTickAsync(tick, symbol);

                // Обрабатываем торговые решения
                await ProcessTradingDecisionAsync(symbol, decision);
            }
            catch (Exception ex)
            {
                LogError($"Error processing price update for {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработка торговых решений
        /// </summary>
        private async Task ProcessTradingDecisionAsync(string symbol, TradingDecision decision)
        {
            try
            {
                LogInfo($"🔄 Processing trading decision for {symbol}: {decision.Action}");
                
                switch (decision.Action)
                {
                    case TradingAction.OpenPosition:
                        if (decision.Signal.HasValue)
                        {
                            LogInfo($"📈 Attempting to open position for {symbol}");
                            await ProcessOpenPositionAsync(symbol, decision.Signal.Value);
                        }
                        else
                        {
                            LogWarning($"❌ Open position signal is null for {symbol}");
                        }
                        break;

                    case TradingAction.ClosePosition:
                        LogInfo($"📉 Attempting to close position for {symbol}");
                        await ProcessClosePositionAsync(symbol, "Strategy signal");
                        break;

                    case TradingAction.PartialClose:
                        LogInfo($"📊 Attempting partial close for {symbol} at {decision.Percentage:P}");
                        await ProcessPartialCloseAsync(symbol, decision.Percentage);
                        break;
                        
                    case TradingAction.NoAction:
                        // Не логируем NoAction для чистоты логов
                        break;
                        
                    default:
                        LogWarning($"❓ Unknown trading action: {decision.Action} for {symbol}");
                        break;
                }
            }
            catch (Exception ex)
            {
                LogError($"Error processing trading decision for {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Открытие позиции
        /// </summary>
        private async Task ProcessOpenPositionAsync(string symbol, OBIZSignal signal)
        {
            if (!_autoConfig.EnableAutoTrading) return;

            try
            {
                // Проверяем возможность открытия
                if (!_positionManager.CanOpenPosition(symbol, signal))
                {
                    return;
                }

                // Открываем позицию
                var openResult = await _positionManager.OpenPositionAsync(symbol, signal);
                if (openResult.Success)
                {
                    LogInfo($"🎯 Position opened: {openResult.Symbol} {openResult.Direction} at {openResult.EntryPrice:F4}");
                    OnPositionOpened?.Invoke(symbol, signal.Direction.ToString());
                    OnOBIZSignal?.Invoke(symbol, signal);
                }
                else
                {
                    LogWarning($"Failed to open position for {symbol}: {openResult.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                LogError($"Error opening position for {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Закрытие позиции
        /// </summary>
        private async Task ProcessClosePositionAsync(string symbol, string reason)
        {
            try
            {
                var coinData = _dataStorage.GetCoinData(symbol);
                if (coinData == null) return;

                var closeResult = await _positionManager.ClosePositionAsync(symbol, coinData.CurrentPrice, reason);
                if (closeResult.Success)
                {
                    LogInfo($"🏁 Position closed: {closeResult.Symbol} | PnL: {closeResult.PnLPercent:F2}% | Reason: {reason}");
                    OnPositionClosed?.Invoke(symbol, $"PnL: {closeResult.PnLPercent:F2}%");
                }
            }
            catch (Exception ex)
            {
                LogError($"Error closing position for {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Частичное закрытие позиции
        /// </summary>
        private async Task ProcessPartialCloseAsync(string symbol, decimal percentage)
        {
            try
            {
                var coinData = _dataStorage.GetCoinData(symbol);
                if (coinData == null) return;

                var partialResult = await _positionManager.PartialClosePositionAsync(symbol, coinData.CurrentPrice, percentage);
                if (partialResult.Success)
                {
                    LogInfo($"📊 Partial close: {partialResult.Symbol} {percentage:P0} | PnL: {partialResult.PnLPercent:F2}%");
                }
            }
            catch (Exception ex)
            {
                LogError($"Error partial closing position for {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Callback для анализа (каждые 500ms)
        /// </summary>
        private void AnalysisCallback(object? state)
        {
            if (!_isRunning) return;

            Task.Run(async () =>
            {
                try
                {
                    // Обновляем статистики стратегий
                    foreach (var (symbol, strategy) in _strategies)
                    {
                        var stats = strategy.GetCurrentStats();
                        
                        // Логируем детальную информацию если включено
                        if (_obizConfig.EnableDetailedLogging && DateTime.UtcNow.Second % 10 == 0)
                        {
                            LogInfo($"📊 {symbol}: OBIZ={stats.CurrentOBIZScore:F2}, Activity={stats.CurrentActivityScore:F2}, Regime={stats.CurrentRegime}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Error in analysis callback: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Callback для обновления позиций (каждую секунду)
        /// </summary>
        private void PositionUpdateCallback(object? state)
        {
            if (!_isRunning) return;

            Task.Run(async () =>
            {
                try
                {
                    // Получаем текущие цены
                    var currentPrices = new Dictionary<string, decimal>();
                    foreach (var symbol in _activeSymbols)
                    {
                        var coinData = _dataStorage.GetCoinData(symbol);
                        if (coinData != null)
                        {
                            currentPrices[symbol] = coinData.CurrentPrice;
                        }
                    }

                    // Обновляем все позиции
                    var updateResults = await _positionManager.UpdateAllPositionsAsync(currentPrices);
                    
                    foreach (var result in updateResults.Where(r => r.ActionTaken))
                    {
                        LogInfo($"📈 Position update: {result.Symbol} - {result.Action}");
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Error in position update callback: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Закрытие всех позиций
        /// </summary>
        private async Task CloseAllPositionsAsync(string reason)
        {
            try
            {
                var currentPrices = new Dictionary<string, decimal>();
                foreach (var symbol in _activeSymbols)
                {
                    var coinData = _dataStorage.GetCoinData(symbol);
                    if (coinData != null)
                    {
                        currentPrices[symbol] = coinData.CurrentPrice;
                    }
                }

                var openPositions = _positionManager.GetAllPositions(currentPrices);
                foreach (var position in openPositions)
                {
                    await ProcessClosePositionAsync(position.Symbol, reason);
                }
            }
            catch (Exception ex)
            {
                LogError($"Error closing all positions: {ex.Message}");
            }
        }

        /// <summary>
        /// Получение статистики сервиса
        /// </summary>
        public OBIZServiceStats GetStats()
        {
            var currentPrices = new Dictionary<string, decimal>();
            foreach (var symbol in _activeSymbols)
            {
                var coinData = _dataStorage.GetCoinData(symbol);
                if (coinData != null)
                {
                    currentPrices[symbol] = coinData.CurrentPrice;
                }
            }

            return new OBIZServiceStats
            {
                IsRunning = _isRunning,
                ActiveSymbols = _activeSymbols.Count,
                ActiveStrategies = _strategies.Count,
                PositionStats = _positionManager.GetStatistics(),
                OpenPositions = _positionManager.GetAllPositions(currentPrices)
            };
        }

        public void Dispose()
        {
            _analysisTimer?.Dispose();
            _positionUpdateTimer?.Dispose();
        }

        private void LogInfo(string message)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] OBIZ_SERVICE: {message}");
        }

        private void LogWarning(string message)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] OBIZ_SERVICE WARNING: {message}");
        }

        private void LogError(string message)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] OBIZ_SERVICE ERROR: {message}");
            OnError?.Invoke(message);
        }
    }

    /// <summary>
    /// Статистика OBIZ сервиса
    /// </summary>
    public class OBIZServiceStats
    {
        public bool IsRunning { get; set; }
        public int ActiveSymbols { get; set; }
        public int ActiveStrategies { get; set; }
        public PositionStatistics PositionStats { get; set; } = new();
        public List<PositionInfo> OpenPositions { get; set; } = new();
    }
}
