# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

BotPiu is an autonomous cryptocurrency trading bot built with C# and .NET 8.0, designed for high-frequency trading on Binance. The system features an advanced multi-strategy approach with a primary focus on the OBIZ-Score strategy for scalping opportunities.

## Commands

### Build and Run
- `dotnet build` - Build the project
- `dotnet run` - Run the autonomous trading system (requires API keys in .env)
- `dotnet run --configuration Release` - Build and run in release mode

### Testing Commands
The application includes comprehensive testing via command-line arguments:

- `dotnet run test-pool` - Test coin pool collection and filtering
- `dotnet run test-websocket` - Test WebSocket real-time data connections
- `dotnet run test-strategy` - Test trading strategy and signal generation
- `dotnet run test-hft` - Test pseudo-HFT system components
- `dotnet run test-auto` - Test automated trading functionality
- `dotnet run test-all` - Run all tests sequentially
- `dotnet run test-coins` - Test coin selection service
- `dotnet run test-obiz` - Test OBIZ-Score strategy integration
- `dotnet run test-components` - Test OBIZ strategy components (interactive)

## Architecture

### Core System Design

The bot operates through a layered architecture centered around the **AutonomousEngine** which provides:
- Automatic crash recovery with exponential backoff
- Persistent state management 
- System health monitoring and logging

### Key Services

1. **AutonomousEngine** (`Services/AutonomousEngine.cs`) - Main orchestrator with auto-recovery
2. **AutoTradingService** - Coordinates HFT analysis with trading execution
3. **TradingStrategyService** - Unified strategy coordinator supporting multiple approaches
4. **HftSignalEngineService** - High-frequency signal generation and analysis
5. **OBIZ Strategy Suite** (`Services/OBIZScore/`) - Advanced scalping strategy using statistical analysis

### Strategy System

The bot supports dual strategy modes configured via `config.json`:

**Legacy Mode**: Traditional indicators (SMA, RSI, volume analysis)
**OBIZ Mode**: Advanced statistical strategy using Z-score, VWAP deviation, and order book analysis

OBIZ Strategy components:
- **OBIZScoreStrategy** - Core scoring algorithm
- **IntegratedStrategyService** - Integration with existing trading infrastructure  
- **OBIZPositionManager** - Specialized position management with adaptive take-profit/stop-loss
- **TickDataAdapter** - Real-time data processing for microsecond precision

### Data Management

- **DataStorageService** - In-memory data storage with persistence
- **BinanceDataService** - API integration and rate limiting
- **MultiSymbolWebSocketService** - Real-time market data streams
- **FifteenSecondCandleService** - Sub-minute timeframe analysis

### Configuration System

Configuration is managed through `config.json` with these main sections:
- `Trading` - Core trading parameters (amounts, take-profit, stop-loss)
- `AutoTrading` - Concurrent positions and timing controls
- `CoinSelection` - Manual/Auto coin selection modes
- `Strategy` - Strategy enabling and weighting
- `OBIZStrategy` - OBIZ-specific parameters and thresholds
- `Backend` - Data collection and filtering parameters

### State Management

The system uses **SimpleStateManager** for:
- Persistent position tracking across restarts
- System event logging with JSON structured format
- Recovery state management for the autonomous engine

### Coin Selection

Two modes available:
- **Manual**: Trade only specified symbols in `config.json`
- **Auto**: Dynamic selection based on volume, volatility (NATR), and other criteria

### WebSocket Architecture

Real-time data handled through:
- Multi-symbol subscription management
- Automatic reconnection with exponential backoff
- Tick-level data processing for OBIZ strategy
- Kline and trade stream integration

## Development Notes

### Testing Infrastructure

- **UniversalTester** - Comprehensive testing suite in main program
- **ComponentTester** - OBIZ strategy component testing
- **OBIZIntegrationTest** - Full integration testing

### Logging System

- **JsonLogger** - Structured JSON logging for system events
- **OBIZJsonLogger** - Specialized logging for OBIZ strategy events
- Event-driven logging with categorized message types

### Error Handling

The AutonomousEngine implements sophisticated error recovery:
- Maximum restart attempts with cooldown periods
- Differentiation between recoverable and fatal errors
- State persistence across crashes
- Graceful shutdown handling

### API Integration

Uses Binance.Net library v9.0.3 with:
- REST API for account operations and historical data
- WebSocket streams for real-time market data
- Proper rate limiting and error handling
- Authentication via environment variables (.env file)

## Configuration Management

Key configuration files:
- `.env` - API credentials (not in repository)
- `config.json` - All trading and strategy parameters  
- `bot_state.json` - Runtime state persistence

The system supports hot-reloading of configuration and maintains backward compatibility across strategy modes.