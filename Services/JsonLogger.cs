using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Services
{
    /// <summary>
    /// Централизованный JSON-логгер для структурированного вывода логов
    /// </summary>
    public static class JsonLogger
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// Базовая структура лог-записи
        /// </summary>
        public class LogEntry
        {
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
            public string Level { get; set; } = "INFO";
            public string Component { get; set; } = "SYSTEM";
            public string Message { get; set; } = "";
            public Dictionary<string, object>? Data { get; set; }
            public string? Error { get; set; }
            public string? StackTrace { get; set; }
        }

        /// <summary>
        /// Основной метод логирования
        /// </summary>
        public static void Log(string level, string component, string message, Dictionary<string, object>? data = null, Exception? exception = null)
        {
            var entry = new LogEntry
            {
                Level = level,
                Component = component,
                Message = message,
                Data = data,
                Error = exception?.Message,
                StackTrace = exception?.StackTrace
            };

            var json = JsonSerializer.Serialize(entry, _jsonOptions);
            Console.WriteLine(json);
        }

        // Удобные методы для разных уровней логирования
        public static void Info(string component, string message, Dictionary<string, object>? data = null)
            => Log("INFO", component, message, data);

        public static void Success(string component, string message, Dictionary<string, object>? data = null)
            => Log("SUCCESS", component, message, data);

        public static void Warning(string component, string message, Dictionary<string, object>? data = null)
            => Log("WARNING", component, message, data);

        public static void Error(string component, string message, Dictionary<string, object>? data = null, Exception? exception = null)
            => Log("ERROR", component, message, data, exception);

        public static void Debug(string component, string message, Dictionary<string, object>? data = null)
            => Log("DEBUG", component, message, data);

        // Специализированные методы для торговой системы
        public static void TradeOpened(string symbol, string side, decimal amount, decimal price, Dictionary<string, object>? data = null)
        {
            var tradeData = new Dictionary<string, object>
            {
                ["symbol"] = symbol,
                ["side"] = side,
                ["amount"] = amount,
                ["price"] = price
            };
            
            if (data != null)
            {
                foreach (var kvp in data)
                    tradeData[kvp.Key] = kvp.Value;
            }

            Log("TRADE_OPENED", "TRADING", $"Position opened: {symbol} {side}", tradeData);
        }

        public static void TradeClosed(string symbol, string result, Dictionary<string, object>? data = null)
        {
            var tradeData = new Dictionary<string, object>
            {
                ["symbol"] = symbol,
                ["result"] = result
            };
            
            if (data != null)
            {
                foreach (var kvp in data)
                    tradeData[kvp.Key] = kvp.Value;
            }

            Log("TRADE_CLOSED", "TRADING", $"Position closed: {symbol} - {result}", tradeData);
        }

        public static void TradingSignal(string symbol, string signal, decimal price, decimal zScore, decimal? natr = null, Dictionary<string, object>? data = null)
        {
            var signalData = new Dictionary<string, object>
            {
                ["symbol"] = symbol,
                ["signal"] = signal,
                ["price"] = price,
                ["zScore"] = zScore
            };

            if (natr.HasValue)
                signalData["natr"] = natr.Value;
            
            if (data != null)
            {
                foreach (var kvp in data)
                    signalData[kvp.Key] = kvp.Value;
            }

            Log("TRADING_SIGNAL", "STRATEGY", $"{signal} signal for {symbol}", signalData);
        }

        public static void UniverseUpdate(int totalFound, int filtered, int newAdded, double durationSeconds, Dictionary<string, object>? data = null)
        {
            var updateData = new Dictionary<string, object>
            {
                ["totalFound"] = totalFound,
                ["filtered"] = filtered,
                ["newAdded"] = newAdded,
                ["durationSeconds"] = durationSeconds
            };
            
            if (data != null)
            {
                foreach (var kvp in data)
                    updateData[kvp.Key] = kvp.Value;
            }

            Log("UNIVERSE_UPDATE", "DATA_SERVICE", "Universe update completed", updateData);
        }

        public static void SystemEvent(string eventType, string message, Dictionary<string, object>? data = null)
        {
            var eventData = new Dictionary<string, object>
            {
                ["eventType"] = eventType
            };
            
            if (data != null)
            {
                foreach (var kvp in data)
                    eventData[kvp.Key] = kvp.Value;
            }

            Log("SYSTEM_EVENT", "SYSTEM", message, eventData);
        }

        public static void WebSocketEvent(string symbol, string eventType, Dictionary<string, object>? data = null)
        {
            var wsData = new Dictionary<string, object>
            {
                ["symbol"] = symbol,
                ["eventType"] = eventType
            };
            
            if (data != null)
            {
                foreach (var kvp in data)
                    wsData[kvp.Key] = kvp.Value;
            }

            Log("WEBSOCKET_EVENT", "WEBSOCKET", $"WebSocket event: {eventType} for {symbol}", wsData);
        }

        public static void PerformanceMetric(string metricName, double value, string unit = "", Dictionary<string, object>? data = null)
        {
            var perfData = new Dictionary<string, object>
            {
                ["metricName"] = metricName,
                ["value"] = value,
                ["unit"] = unit
            };
            
            if (data != null)
            {
                foreach (var kvp in data)
                    perfData[kvp.Key] = kvp.Value;
            }

            Log("PERFORMANCE", "METRICS", $"{metricName}: {value} {unit}", perfData);
        }

        // Метод для совместимости - постепенная миграция от обычных Console.WriteLine
        public static void LegacyLog(string originalMessage, string component = "LEGACY")
        {
            // Попытка извлечь информацию из старого формата логов
            var level = "INFO";
            
            if (originalMessage.Contains("❌") || originalMessage.Contains("ERROR"))
                level = "ERROR";
            else if (originalMessage.Contains("⚠️") || originalMessage.Contains("WARNING"))
                level = "WARNING";
            else if (originalMessage.Contains("✅") || originalMessage.Contains("SUCCESS"))
                level = "SUCCESS";
            else if (originalMessage.Contains("🚀") || originalMessage.Contains("START"))
                level = "INFO";

            // Удаляем timestamp если он есть в начале
            var cleanMessage = originalMessage;
            if (originalMessage.StartsWith("[") && originalMessage.Contains("]"))
            {
                var endBracket = originalMessage.IndexOf("]");
                if (endBracket > 0 && endBracket < 20)
                    cleanMessage = originalMessage.Substring(endBracket + 1).Trim();
            }

            Log(level, component, cleanMessage);
        }
    }
}






