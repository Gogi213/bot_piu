using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Services.OBIZScore
{
    /// <summary>
    /// JSON логгер специально для OBIZ стратегии с записью в файл
    /// </summary>
    public static class OBIZJsonLogger
    {
        private static readonly string LogDirectory = "logs";
        private static readonly string LogFileName = "obiz_strategy.json";
        private static readonly object _lockObject = new object();
        
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        static OBIZJsonLogger()
        {
            // Создаем директорию логов если её нет
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
        }

        /// <summary>
        /// Основной метод логирования в файл и консоль
        /// </summary>
        public static void Log(string level, string component, string message, Dictionary<string, object>? data = null)
        {
            var logEntry = new
            {
                timestamp = DateTime.UtcNow,
                level = level,
                component = component,
                message = message,
                data = data
            };

            var jsonLog = JsonSerializer.Serialize(logEntry, _jsonOptions);
            
            // Вывод в консоль
            Console.WriteLine(jsonLog);
            
            // Запись в файл (thread-safe)
            Task.Run(() => WriteToFileAsync(jsonLog));
        }

        private static async Task WriteToFileAsync(string jsonLog)
        {
            try
            {
                var filePath = Path.Combine(LogDirectory, LogFileName);
                
                lock (_lockObject)
                {
                    File.AppendAllText(filePath, jsonLog + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                // Если не можем записать в файл, хотя бы выводим ошибку в консоль
                Console.WriteLine($"ERROR: Failed to write log to file: {ex.Message}");
            }
        }

        // Удобные методы для разных уровней
        public static void Info(string component, string message, Dictionary<string, object>? data = null)
            => Log("INFO", component, message, data);

        public static void Debug(string component, string message, Dictionary<string, object>? data = null)
            => Log("DEBUG", component, message, data);

        public static void Error(string component, string message, Dictionary<string, object>? data = null)
            => Log("ERROR", component, message, data);

        public static void Warning(string component, string message, Dictionary<string, object>? data = null)
            => Log("WARNING", component, message, data);

        /// <summary>
        /// Создает новый файл логов (архивирует старый)
        /// </summary>
        public static void RotateLogFile()
        {
            try
            {
                var currentFile = Path.Combine(LogDirectory, LogFileName);
                if (File.Exists(currentFile))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var archiveFile = Path.Combine(LogDirectory, $"obiz_strategy_{timestamp}.json");
                    File.Move(currentFile, archiveFile);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to rotate log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Логирование сигнала с полными данными
        /// </summary>
        public static void LogSignal(string symbol, string signalType, Dictionary<string, object> signalData)
        {
            var data = new Dictionary<string, object>(signalData)
            {
                ["symbol"] = symbol,
                ["signalType"] = signalType
            };
            
            Log("SIGNAL", "OBIZ_STRATEGY", $"Signal: {signalType} for {symbol}", data);
        }

        /// <summary>
        /// Логирование метрик стратегии
        /// </summary>
        public static void LogMetrics(string symbol, Dictionary<string, object> metrics)
        {
            var data = new Dictionary<string, object>(metrics)
            {
                ["symbol"] = symbol
            };
            
            Log("METRICS", "OBIZ_STRATEGY", $"Metrics update for {symbol}", data);
        }
    }
}

