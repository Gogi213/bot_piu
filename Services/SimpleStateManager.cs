using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Config;

namespace Services
{
    /// <summary>
    /// Простой менеджер состояния через JSON файлы (вместо SQLite)
    /// </summary>
    public class SimpleStateManager
    {
        private readonly string _stateFile = "bot_state.json";
        private readonly string _logFile = "bot_events.log";

        /// <summary>
        /// Состояние бота
        /// </summary>
        public class BotState
        {
            public Dictionary<string, ActivePosition> ActivePositions { get; set; } = new();
            public Dictionary<string, DateTime> LastTradeTime { get; set; } = new();
            public Dictionary<string, string> LastSignals { get; set; } = new();
            public DateTime LastSaved { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Активная позиция (упрощенная версия)
        /// </summary>
        public class ActivePosition
        {
            public string Symbol { get; set; } = string.Empty;
            public string Side { get; set; } = string.Empty;
            public decimal UsdAmount { get; set; }
            public decimal EntryPrice { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
            public TradingConfig TradingConfig { get; set; } = new();
        }

        /// <summary>
        /// Запись истории торговли
        /// </summary>
        public class TradeHistoryRecord
        {
            public string Symbol { get; set; } = string.Empty;
            public string Side { get; set; } = string.Empty;
            public decimal UsdAmount { get; set; }
            public decimal EntryPrice { get; set; }
            public string Result { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public DateTime ClosedAt { get; set; }
            public TimeSpan Duration { get; set; }
        }

        /// <summary>
        /// Сохранение состояния
        /// </summary>
        public async Task SaveStateAsync(BotState state)
        {
            try
            {
                state.LastSaved = DateTime.UtcNow;
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_stateFile, json);
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💾 Состояние сохранено: {state.ActivePositions.Count} позиций");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка сохранения состояния: {ex.Message}");
                await LogEventAsync("STATE_SAVE_ERROR", ex.Message);
            }
        }

        /// <summary>
        /// Загрузка состояния
        /// </summary>
        public async Task<BotState> LoadStateAsync()
        {
            try
            {
                if (!File.Exists(_stateFile))
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💾 Файл состояния не найден, создается новый");
                    return new BotState();
                }

                var json = await File.ReadAllTextAsync(_stateFile);
                var state = JsonSerializer.Deserialize<BotState>(json) ?? new BotState();
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 💾 Состояние загружено: {state.ActivePositions.Count} позиций");
                return state;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ Ошибка загрузки состояния: {ex.Message}");
                await LogEventAsync("STATE_LOAD_ERROR", ex.Message);
                return new BotState();
            }
        }

        /// <summary>
        /// Простое логирование событий в файл
        /// </summary>
        public async Task LogEventAsync(string eventType, string message)
        {
            try
            {
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {eventType}: {message}";
                await File.AppendAllTextAsync(_logFile, logEntry + Environment.NewLine);
                
                // Ротация лога при превышении 10MB
                var fileInfo = new FileInfo(_logFile);
                if (fileInfo.Exists && fileInfo.Length > 10 * 1024 * 1024)
                {
                    var backupFile = $"bot_events_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                    File.Move(_logFile, backupFile);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📁 Лог файл архивирован: {backupFile}");
                }
            }
            catch
            {
                // Игнорируем ошибки логирования
            }
        }

        // Методы для совместимости с AutoTradingService
        public async Task SaveActivePositionAsync(ActivePosition position)
        {
            var state = await LoadStateAsync();
            state.ActivePositions[position.Symbol] = position;
            await SaveStateAsync(state);
        }

        public async Task RemoveActivePositionAsync(string symbol)
        {
            var state = await LoadStateAsync();
            state.ActivePositions.Remove(symbol);
            await SaveStateAsync(state);
        }

        public async Task SaveTradingStateAsync(string symbol, DateTime lastTradeTime, string signal)
        {
            var state = await LoadStateAsync();
            state.LastTradeTime[symbol] = lastTradeTime;
            state.LastSignals[symbol] = signal;
            await SaveStateAsync(state);
        }

        public async Task<Dictionary<string, ActivePosition>> LoadActivePositionsAsync()
        {
            var state = await LoadStateAsync();
            return state.ActivePositions;
        }

        public async Task<Dictionary<string, DateTime>> LoadTradingStateAsync()
        {
            var state = await LoadStateAsync();
            return state.LastTradeTime;
        }

        public async Task SaveTradeHistoryAsync(TradeHistoryRecord trade)
        {
            await LogEventAsync("TRADE_COMPLETED", 
                $"{trade.Symbol} {trade.Side} {trade.UsdAmount}USDT -> {trade.Result} (Duration: {trade.Duration.TotalMinutes:F1}min)");
        }

        public async Task LogSystemEventAsync(string eventType, string message, string? stackTrace = null)
        {
            var fullMessage = string.IsNullOrEmpty(stackTrace) ? message : $"{message}\nStackTrace: {stackTrace}";
            await LogEventAsync(eventType, fullMessage);
        }
    }
}
