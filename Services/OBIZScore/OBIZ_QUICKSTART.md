# 🚀 OBIZ-Score Strategy - Quick Start Guide

## ✅ **ГОТОВО К ЗАПУСКУ!**

OBIZ-Score стратегия реализована как **полностью автономный модуль** и готова к работе!

## 🎯 **Активация OBIZ (3 шага)**

### 1️⃣ **Включите OBIZ в config.json:**
```json
{
  "Strategy": {
    "EnableOBIZStrategy": true,
    "Mode": "OBIZOnly"
  }
}
```

### 2️⃣ **Тестирование (опционально):**
```bash
dotnet run test-obiz
```

### 3️⃣ **Запуск торговли:**
```bash
dotnet run
```

## 🏗️ **Архитектура: Полностью автономная**

```
Program.cs
└── AutonomousEngine
    ├── Legacy стратегии (если Mode ≠ "OBIZOnly")
    └── OBIZAutonomousService (если Mode = "OBIZOnly")
        ├── OBIZScoreStrategy[] (по одной на символ)
        ├── OBIZPositionManager
        ├── TickDataAdapter
        └── Real-time WebSocket
```

## 📊 **Что происходит при запуске OBIZ:**

1. **🔍 Выбор монет** - автоматически или по вашему списку
2. **🧠 Инициализация стратегий** - по одной OBIZ на каждый символ
3. **📈 Прогрев историческими данными** - последние 50 свечей → тики
4. **📡 WebSocket реал-тайм** - обновления цен каждые миллисекунды  
5. **⚡ Анализ каждые 500ms** - OBIZ метрики для всех символов
6. **🎯 Торговые сигналы** - автоматическое открытие/закрытие позиций

## 🎛️ **Управление монетами**

### Автоматический отбор:
```json
"CoinSelection": {
  "Mode": "Auto"
}
```
- Фильтрация по объему (100M USDT) и волатильности (0.55%)

### Ручной выбор:
```json
"CoinSelection": {
  "Mode": "Manual",
  "ManualCoins": ["BTCUSDT", "ETHUSDT", "ADAUSDT"]
}
```

## 📊 **Мониторинг в реальном времени**

### Сигналы OBIZ:
```
🎯 OBIZ SIGNAL: BTCUSDT Buy | Score: 2.34 | Confidence: High | Regime: Choppy
✅ OBIZ POSITION OPENED: BTCUSDT Buy
🏁 OBIZ POSITION CLOSED: BTCUSDT (PnL: +1.24%)
```

### Статистика каждые 30 секунд:
```
📊 OBIZ Status: Strategies: 5, Positions: 2/10, Symbols: 5
```

## ⚙️ **Конфигурация OBIZ (основные параметры)**

```json
"OBIZStrategy": {
  "ZScoreThreshold": 2.0,           // Порог входа
  "StrongZScoreThreshold": 2.5,     // Усиленный порог
  "BaseTakeProfit": 0.0013,         // TP: 0.13% (1.3 RR)
  "BaseStopLoss": 0.001,            // SL: 0.1% 
  "MaxHoldTimeSeconds": 300,        // Макс 5 минут в позиции
  "EnableDetailedLogging": false    // Детальные логи
}
```

## 🧪 **Команды тестирования**

```bash
dotnet run test-obiz    # Тест OBIZ стратегии
dotnet run test-coins   # Тест выбора монет
dotnet run test-pool    # Тест сбора данных
```

## 🔄 **Режимы работы**

| Режим | config.json | Что работает |
|-------|-------------|--------------|
| **Legacy** | `"Mode": "Legacy"` | Старые стратегии (Z-Score + SMA) |
| **OBIZOnly** | `"Mode": "OBIZOnly"` | **Только OBIZ (автономно)** |
| **Combined** | `"Mode": "Combined"` | Пока не реализовано |

## 🎯 **Готовые настройки для запуска**

### Консервативная конфигурация:
```json
{
  "Strategy": {
    "EnableOBIZStrategy": true,
    "Mode": "OBIZOnly"
  },
  "CoinSelection": {
    "Mode": "Manual",
    "ManualCoins": ["BTCUSDT", "ETHUSDT", "ADAUSDT"]
  },
  "OBIZStrategy": {
    "ZScoreThreshold": 2.5,
    "MaxHoldTimeSeconds": 180,
    "EnableDetailedLogging": true
  },
  "AutoTrading": {
    "MaxConcurrentPositions": 3
  }
}
```

### Агрессивная конфигурация:
```json
{
  "Strategy": {
    "EnableOBIZStrategy": true,
    "Mode": "OBIZOnly"
  },
  "CoinSelection": {
    "Mode": "Auto"
  },
  "OBIZStrategy": {
    "ZScoreThreshold": 1.8,
    "MaxHoldTimeSeconds": 300,
    "EnableDetailedLogging": false
  },
  "AutoTrading": {
    "MaxConcurrentPositions": 10
  }
}
```

## 🚨 **Важные моменты**

1. **Отдельный модуль** - OBIZ работает полностью автономно
2. **Не смешивается** с Legacy стратегиями при Mode = "OBIZOnly"
3. **Реальные тики** симулируются из WebSocket данных
4. **Адаптивные TP/SL** корректируются под волатильность
5. **Risk Management** - максимум 2% риска на сделку

## ✅ **Статус готовности**

- ✅ **OBIZ стратегия** - 100% реализована
- ✅ **Автономный сервис** - готов к запуску
- ✅ **Конфигурация** - настроена  
- ✅ **Тестирование** - комплексное покрытие
- ✅ **Документация** - полная инструкция
- ✅ **Мониторинг** - реал-тайм логи

## 🚀 **Быстрый старт прямо сейчас:**

1. Установите в config.json: `"Mode": "OBIZOnly", "EnableOBIZStrategy": true`
2. Запустите: `dotnet run`
3. Наблюдайте за OBIZ сигналами в консоли

**OBIZ готова к торговле! 🎯**
