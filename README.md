Fork of https://github.com/oliverw/miningcore/ to support Unicity Proof of Work 

## Quick Start

For complete setup instructions including Alpha node configuration, database setup, web frontend, and payment processing, see the **[Complete Mining Pool Setup Guide](MINING-POOL-SETUP-GUIDE.md)**.

## Unicity Proof of Work Implementation

This fork builds on Miningcore into a **Unicity Proof of Work** mining pool with comprehensive support for the Unicity Consensus Layer Blockchain.

###  Core Technical Changes

**Complete Unicity Alpha Blockchain Implementation**

- **112-byte Headers**: Extended from Bitcoin's 80 bytes to include RandomX hash field
- **Epoch-Based Seeds**: Dynamic RandomX key generation using `Alpha/RandomX/Epoch/{epoch}`
- **Custom Difficulty Algorithm**: Matches Alpha daemon's exact calculation

**RandomX Native Integration**

- **VM Pool Management**: Thread-safe RandomX VM borrowing/returning with realm isolation
- **Memory Optimization**: Efficient VM reuse and cache management (256MB-3GB per VM)
- **CPU Optimization**: Hardware-specific compilation with AES/AVX/SSE flags
- **Commitment Hash Calculation**: New `randomx_calculate_commitment()` function

**Extended Block Structure**

```
Alpha Block: StandardHeader(80) + RandomXHash(32) + TransactionData
Bitcoin Block: StandardHeader(80) + TransactionData
```

**Custom Address Format**

- Native "alpha1" Bech32 addresses with custom HRP
- Full address validation and destination resolution

###  Build System Enhancements

**Automated RandomX Building**

- Downloads and compiles Unicity's RandomX fork from `github.com/unicitynetwork/RandomX`
- Cross-platform support (Linux compilation, Windows pre-built libraries)
- CPU feature detection and optimization

**Enhanced Project Structure**

```
src/
├── Miningcore/
│   ├── Blockchain/Alpha/           # Unicity Alpha blockchain implementation
│   ├── Api/                        # REST API with admin endpoints
│   ├── Native/RandomX.cs           # RandomX P/Invoke interface
│   ├── Persistence/Postgres/       # Database schema and repositories
│   └── coins.json                  # Alpha coin definition
├── PaymentProcessor/               # Standalone payment application
│   ├── Configuration/              # Configuration classes
│   ├── Models/                     # Data models and RPC types
│   ├── Services/                   # Core payment services
│   └── appsettings.json            # Payment processor configuration
└── Native/librandomx/              # RandomX native library
```

###  Configuration & Deployment

**Alpha Coin Configuration**

```json
{
    "alpha": {
        "name": "Alpha",
        "family": "bitcoin",
        "headerHasher": null,
        "blockHasher": {"hash": "sha256d"},
        "bech32Hrp": "alpha",
        "hashrateMultiplier": 0.0625,
        "blockTime": 120
    }
}
```

**Pool Configuration Features**

- **Fixed Block Rewards**: 10 ALPHA per block
- **External Payment Processing**: Queued payments for distributed processing
- **Blockchain-Level Operations**: No local wallet dependency required
- **ZeroMQ Integration**: Real-time block notifications

###  PaymentProcessor Application

**Secure, Cross-Platform Payment Processing**

The PaymentProcessor is a standalone .NET application designed for secure, automated miner payouts:

**Architecture Benefits**

- **Machine Isolation**: Runs on separate secured machine from pool server
- **Cross-Platform**: Compatible with Windows, macOS, and Linux (.NET 6+)
- **Wallet Security**: Direct Alpha daemon integration with wallet management
- **API Integration**: Secure connection to pool server via API keys

**Key Features**

- **Interactive Console**: Select specific payments to process
- **Unicity Alpha Blockchain Integration**: Native RPC client with wallet support
- **Transaction Management**: UTXO selection, fee calculation, change handling
- **Comprehensive Logging**: Structured logging with Serilog
- **Error Handling**: Robust error recovery and validation

**Security Architecture**

```
Pool Server (Linux)          Payment Machine (Any OS)
├── Miningcore               ├── Alpha Wallet
├── PostgreSQL               ├── PaymentProcessor
├── API Server               └── Secure API Connection
└── Payment Queue            
```

**Payment Flow**

1. Pool server queues payments in database
2. PaymentProcessor fetches pending payments via API
3. User selects payments to process interactively
4. Transactions created and broadcast to Alpha network
5. Payment status updated in pool database

### Performance & Scalability

**Memory Management**

- RandomX VM pooling with automatic cleanup
- Configurable VM count per realm
- Memory-hard hashing for ASIC resistance

**Production Ready**

- Full Stratum protocol compatibility
- Live stats API and WebSocket notifications
- Comprehensive logging and monitoring
- PostgreSQL persistence with partitioned tables

### Development Requirements

**Additional Dependencies for Unicity**

- CMake (for RandomX compilation)
- CPU with AES-NI support (recommended)
- Sufficient RAM for RandomX VMs (256MB+ per VM)

**Runtime Requirements**

- Alpha daemon with `rx_epoch_duration` support
- ZeroMQ for block notifications
- PostgreSQL for production persistence

***********************************

