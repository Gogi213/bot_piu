using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Models;
using Config;

namespace Services
{
    /// <summary>
    /// Сервис для выбора монет для торговли
    /// Поддерживает автоматический и ручной режимы
    /// </summary>
    public class CoinSelectionService
    {
        private readonly CoinSelectionConfig _config;
        private readonly BackendConfig _backendConfig;
        private readonly DataStorageService _dataStorage;
        private readonly BinanceDataService _binanceService;

        public CoinSelectionService(
            CoinSelectionConfig config,
            BackendConfig backendConfig,
            DataStorageService dataStorage,
            BinanceDataService binanceService)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _backendConfig = backendConfig ?? throw new ArgumentNullException(nameof(backendConfig));
            _dataStorage = dataStorage ?? throw new ArgumentNullException(nameof(dataStorage));
            _binanceService = binanceService ?? throw new ArgumentNullException(nameof(binanceService));
            
            _config.Validate();
        }

        /// <summary>
        /// Получение списка монет для торговли в зависимости от режима
        /// </summary>
        public async Task<CoinSelectionResult> GetTradingCoinsAsync()
        {
            var result = new CoinSelectionResult
            {
                Mode = _config.Mode,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                switch (_config.Mode)
                {
                    case CoinSelectionMode.Auto:
                        result = await GetAutoSelectedCoinsAsync();
                        break;
                        
                    case CoinSelectionMode.Manual:
                        result = await GetManualSelectedCoinsAsync();
                        break;
                        
                    default:
                        throw new ArgumentException($"Неизвестный режим выбора монет: {_config.Mode}");
                }

                LogInfo($"Coin selection completed: {result}");
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                LogError($"Error in coin selection: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Автоматический отбор монет по фильтрам
        /// </summary>
        private async Task<CoinSelectionResult> GetAutoSelectedCoinsAsync()
        {
            var result = new CoinSelectionResult
            {
                Mode = CoinSelectionMode.Auto,
                Timestamp = DateTime.UtcNow
            };

            // Получаем автоматически отфильтрованные монеты
            var filteredCoins = _dataStorage.GetFilteredCoins(_backendConfig.MinVolumeUsdt, _backendConfig.MinNatrPercent);
            
            result.SelectedCoins = filteredCoins;
            result.TotalCoinsFound = filteredCoins.Count;
            result.Success = true;
            result.SelectionCriteria = $"Volume ≥ {_backendConfig.MinVolumeUsdt:N0} USDT, NATR ≥ {_backendConfig.MinNatrPercent}%";

            LogInfo($"Auto selection: {filteredCoins.Count} coins found with criteria: {result.SelectionCriteria}");
            
            return result;
        }

        /// <summary>
        /// Ручной отбор монет из конфигурации
        /// </summary>
        private async Task<CoinSelectionResult> GetManualSelectedCoinsAsync()
        {
            var result = new CoinSelectionResult
            {
                Mode = CoinSelectionMode.Manual,
                Timestamp = DateTime.UtcNow
            };

            var selectedCoins = new List<CoinData>();
            var missingCoins = new List<string>();
            var validatedCoins = new List<string>();

            LogInfo($"Manual selection: processing {_config.ManualCoins.Count} symbols");

            foreach (var symbol in _config.ManualCoins)
            {
                // Проверяем, существует ли монета в данных
                var coinData = _dataStorage.GetCoinData(symbol);
                
                if (coinData != null)
                {
                    selectedCoins.Add(coinData);
                    validatedCoins.Add(symbol);
                    LogInfo($"✅ {symbol}: found in storage");
                }
                else
                {
                    // Пытаемся получить данные с Binance
                    try
                    {
                        var tickerResponse = await _binanceService.GetSymbolTickerAsync(symbol);
                        if (tickerResponse != null)
                        {
                            // Создаем базовую структуру CoinData для ручных монет
                            var manualCoin = new CoinData
                            {
                                Symbol = symbol,
                                CurrentPrice = tickerResponse.Price,
                                Volume24h = tickerResponse.QuoteVolume,
                                LastUpdated = DateTime.UtcNow,
                                Status = CoinLifecycleStatus.New
                            };
                            
                            selectedCoins.Add(manualCoin);
                            validatedCoins.Add(symbol);
                            _dataStorage.UpdateCoinData(symbol, manualCoin);
                            
                            LogInfo($"✅ {symbol}: fetched from Binance");
                        }
                        else
                        {
                            missingCoins.Add(symbol);
                            LogWarning($"⚠️ {symbol}: not found on Binance");
                        }
                    }
                    catch (Exception ex)
                    {
                        missingCoins.Add(symbol);
                        LogWarning($"❌ {symbol}: error fetching data - {ex.Message}");
                    }
                }
            }

            result.SelectedCoins = selectedCoins;
            result.TotalCoinsFound = selectedCoins.Count;
            result.Success = true;
            result.SelectionCriteria = $"Manual selection: {validatedCoins.Count}/{_config.ManualCoins.Count} symbols found";
            result.MissingSymbols = missingCoins;

            if (missingCoins.Any())
            {
                LogWarning($"Manual selection: {missingCoins.Count} symbols not found: {string.Join(", ", missingCoins)}");
            }

            return result;
        }

        /// <summary>
        /// Получение только символов для торговли
        /// </summary>
        public async Task<List<string>> GetTradingSymbolsAsync()
        {
            var result = await GetTradingCoinsAsync();
            return result.Success 
                ? result.SelectedCoins.Select(c => c.Symbol).ToList()
                : new List<string>();
        }

        /// <summary>
        /// Проверка доступности символа для торговли
        /// </summary>
        public async Task<bool> IsSymbolAvailableAsync(string symbol)
        {
            var tradingCoins = await GetTradingCoinsAsync();
            return tradingCoins.SelectedCoins.Any(c => c.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Обновление конфигурации (для динамического изменения списка)
        /// </summary>
        public void UpdateManualCoins(List<string> newCoins)
        {
            if (_config.Mode == CoinSelectionMode.Manual)
            {
                _config.ManualCoins = newCoins ?? new List<string>();
                _config.Validate();
                LogInfo($"Manual coins updated: {_config.ManualCoins.Count} symbols");
            }
        }

        /// <summary>
        /// Информация о текущей конфигурации
        /// </summary>
        public string GetConfigInfo()
        {
            return _config.ToString();
        }

        private void LogInfo(string message)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] COIN_SELECTION: {message}");
        }

        private void LogWarning(string message)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] COIN_SELECTION WARNING: {message}");
        }

        private void LogError(string message)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] COIN_SELECTION ERROR: {message}");
        }
    }

    /// <summary>
    /// Результат выбора монет для торговли
    /// </summary>
    public class CoinSelectionResult
    {
        public CoinSelectionMode Mode { get; set; }
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; } = false;
        public string ErrorMessage { get; set; } = string.Empty;
        public List<CoinData> SelectedCoins { get; set; } = new List<CoinData>();
        public int TotalCoinsFound { get; set; }
        public string SelectionCriteria { get; set; } = string.Empty;
        public List<string> MissingSymbols { get; set; } = new List<string>();

        public override string ToString()
        {
            var status = Success ? "✅" : "❌";
            var missing = MissingSymbols.Any() ? $", Missing: {MissingSymbols.Count}" : "";
            return $"{status} {Mode} mode: {TotalCoinsFound} coins selected{missing}";
        }
    }
}
