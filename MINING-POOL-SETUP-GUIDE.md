# Complete Mining Pool Setup Guide

This guide provides comprehensive end-to-end instructions for setting up a complete Unicity Alpha mining pool using this Miningcore fork.

## Table of Contents
1. [Prerequisites](#prerequisites)
2. [System Requirements](#system-requirements)
3. [Unicity Consensus Layer (Alpha) Node Setup](#unicity-consensus-layer-alpha-node-setup)
4. [Database Setup](#database-setup)
5. [Miningcore Pool Server Setup](#miningcore-pool-server-setup)
6. [Web Frontend Setup](#web-frontend-setup)
7. [PaymentProcessor Setup (Separate Machine - Any OS)](#paymentprocessor-setup-separate-machine-any-os)
8. [Summary](#summary)
8. [Troubleshooting](#troubleshooting)
9. [Security Best Practices](#security-best-practices)


<a id="prerequisites"></a>
## Prerequisites

### Operating System
- **Linux Only**: Ubuntu 20.04/22.04 
- Minimum 4GB RAM, 8GB+ recommended
- 100GB+ storage space
- Stable internet connection

### Software Dependencies
```bash
# Install build tools and dependencies
sudo apt update
sudo apt install -y build-essential cmake libssl-dev pkg-config \
    libboost-all-dev libsodium-dev libzmq5-dev git curl

# Install .NET 6 SDK (automatically detect Ubuntu version)
UBUNTU_VERSION=$(lsb_release -rs)
echo "📋 Detected Ubuntu version: $UBUNTU_VERSION"

wget https://packages.microsoft.com/config/ubuntu/${UBUNTU_VERSION}/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-6.0

# Fix common .NET installation path issue
sudo cp -r /usr/lib/dotnet/* /usr/share/dotnet/ 2>/dev/null || true

# Verify .NET installation
dotnet --version
```

### Prerequisites Verification
Before proceeding to the next sections, verify all basic components are working:

```bash
# System Pre-flight Check Script
echo "🔍 Running system pre-flight checks..."

# Check Ubuntu version compatibility
UBUNTU_VERSION=$(lsb_release -rs)
echo "📋 Ubuntu version: $UBUNTU_VERSION"

if [[ "$UBUNTU_VERSION" < "20.04" ]]; then
    echo "❌ Ubuntu version $UBUNTU_VERSION not supported (minimum: 20.04)"
    exit 1
else
    echo "✅ Ubuntu version $UBUNTU_VERSION is supported"
fi

# Check system resources
RAM_GB=$(free -g | awk '/^Mem:/{print $2}')
CORES=$(nproc)
DISK_GB=$(df -BG . | awk 'NR==2 {gsub(/G/, "", $4); print int($4)}')

echo ""
echo "📊 System Resources:"
echo "   RAM: ${RAM_GB}GB (minimum: 8GB, recommended: 16GB+)"
echo "   CPU Cores: $CORES (minimum: 4, recommended: 8+)"
echo "   Available Disk: ${DISK_GB}GB (minimum: 100GB)"

# Resource warnings
WARNINGS=0
if [ "$RAM_GB" -lt 8 ]; then
    echo "⚠️  WARNING: RAM is below recommended 8GB"
    ((WARNINGS++))
fi

if [ "$CORES" -lt 4 ]; then
    echo "⚠️  WARNING: CPU cores below recommended 4"
    ((WARNINGS++))
fi

if [ "$DISK_GB" -lt 100 ]; then
    echo "❌ CRITICAL: Insufficient disk space (need 100GB+)"
    exit 1
fi

# Check required tools
echo ""
echo "🔧 Checking development tools:"
REQUIRED_TOOLS="gcc cmake git curl"
for tool in $REQUIRED_TOOLS; do
    if command -v $tool >/dev/null 2>&1; then
        echo "   ✅ $tool: $(command -v $tool)"
    else
        echo "   ❌ $tool: Not found"
        exit 1
    fi
done

# Verify .NET installation
echo ""
echo "🔧 Checking .NET installation:"
if command -v dotnet >/dev/null 2>&1; then
    DOTNET_VERSION=$(dotnet --version)
    echo "   ✅ .NET SDK: $DOTNET_VERSION"
    if [[ "$DOTNET_VERSION" < "6.0" ]]; then
        echo "   ⚠️  WARNING: .NET version $DOTNET_VERSION may be too old (6.0+ recommended)"
        ((WARNINGS++))
    fi
else
    echo "   ❌ .NET SDK: Not found"
    exit 1
fi

echo ""
if [ "$WARNINGS" -eq 0 ]; then
    echo "✅ All system requirements verified - ready to proceed! 🎉"
else
    echo "⚠️  System ready with $WARNINGS warning(s) - consider upgrading hardware for optimal performance"
fi
```

<a id="system-requirements"></a>
## System Requirements

### Hardware Recommendations
- **CPU**: 4+ cores (8+ cores for high-traffic pools)
- **RAM**: 8GB minimum, 16GB+ for production
- **Storage**: SSD recommended, 500GB+ for blockchain data
- **Network**: 100Mbps+ connection, static IP recommended

### Firewall Configuration
```bash
# Open required ports
sudo ufw allow 22/tcp         # SSH
sudo ufw allow 4000/tcp       # Pool API
sudo ufw allow 3052/tcp       # Stratum ports (configurable in config.json)
sudo ufw allow 3053/tcp       # Adjust port numbers based on your
sudo ufw allow 3054/tcp       # config.json port settings
# Note: Unicity Alpha RPC (8589) does not need pulic access 
sudo ufw enable
```

<a id="unicity-consensus-layer-alpha-node-setup"></a>
## Unicity Consensus Layer (Alpha) Node Setup

**Prerequisite**: You must have a fully synchronized Unicity Consensus Layer blockchain node running before setting up the mining pool.

### Install and Configure

1. **Download and install** the Unicity Alpha blockchain node from: https://github.com/unicitynetwork/alpha
2. **Follow the installation instructions** in the Alpha repo
3. **Ensure your Alpha node configuration includes the following required settings**:

```ini
# Required RPC settings
rpcuser=your_rpc_username
rpcpassword=your_rpc_password
rpcport=8589
rpcbind=127.0.0.1
rpcallowip=127.0.0.1

# Required mining settings
server=1
daemon=1
txindex=1

# Required ZMQ settings for real-time notifications
zmqpubhashblock=tcp://127.0.0.1:15101
zmqpubrawtx=tcp://127.0.0.1:28333
```

### Verify Alpha Node Setup
Before proceeding, ensure:
- Alpha node is fully synchronized
- RPC is accessible on port 8589
- ZMQ is configured and running
- Pool wallet is created 
- You have the pool payout address

```bash
# Test RPC connection
curl -u your_rpc_username:your_strong_rpc_password \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"1.0","id":"test","method":"getblockchaininfo","params":[]}' \
  http://localhost:8589/

# Alternative: Test with alpha-cli (if available)
./alpha-cli -rpcuser=your_rpc_username -rpcpassword=your_strong_rpc_password getblockchaininfo

# Verify ZMQ is working
ss -tulpn | grep 15101  # Should show ZMQ hash block port
ss -tulpn | grep 28333  # Should show ZMQ raw transaction port
```

### Security Recommendation: Separate Wallet Machine
**For enhanced security, it is strongly recommended to create and manage the pool wallet on a separate, secured machine rather than on the same server running the mining pool.** This provides several security benefits:

- **Isolation**: Pool server compromise doesn't directly expose wallet funds
- **Air-gapped option**: Wallet machine can be kept offline except for payment processing
- **Access control**: Limited network access to wallet operations
- **Backup security**: Wallet backups can be stored separately from pool infrastructure

The PaymentProcessor can be run on the separate wallet machine and connect to the pool via API to fetch pending payments and process them securely - instructions on how to setup Payment Processor below,.

<a id="database-setup"></a>
## Database Setup

### 1. Install PostgreSQL
```bash
# Install PostgreSQL
sudo apt update
sudo apt install -y postgresql postgresql-contrib
```

### 2. Configure PostgreSQL Authentication

**Important**: Configure md5 authentication to avoid common password issues.

```bash
# Start PostgreSQL service
sudo systemctl start postgresql
sudo systemctl enable postgresql

# Configure authentication method
sudo nano /etc/postgresql/*/main/pg_hba.conf

# Find this line:
# local   all             all                                     peer
# Change it to:
# local   all             all                                     md5

# Fix file permissions after editing as root
sudo chown postgres:postgres /etc/postgresql/*/main/pg_hba.conf

# Restart PostgreSQL to apply authentication changes
sudo systemctl restart postgresql

# Verify PostgreSQL is running
sudo systemctl status postgresql

# Create database user and database (use simple alphanumeric password)
sudo -u postgres psql << EOF
CREATE ROLE miningcore WITH LOGIN ENCRYPTED PASSWORD 'miningcore123';
CREATE DATABASE miningcore OWNER miningcore;
GRANT ALL PRIVILEGES ON DATABASE miningcore TO miningcore;
\q
EOF

# Test database connection
PGPASSWORD=miningcore123 psql -h localhost -U miningcore -d miningcore -c "SELECT version();"
```

### 3. Import Database Schema
```bash
# Clone Miningcore repository (if not already done)
git clone https://github.com/unicitynetwork/unicity-mining-core.git

# Change to the repository directory
cd unicity-mining-core

# Copy SQL file to accessible location for postgres user
cp src/Miningcore/Persistence/Postgres/Scripts/createdb.sql /tmp/

# Import database schema
sudo -u postgres psql -d miningcore -f /tmp/createdb.sql

# Clean up temporary file
rm /tmp/createdb.sql

# Verify database setup was successful
echo "🔍 Verifying database schema..."

# Check that all required tables were created
EXPECTED_TABLES=8
ACTUAL_TABLES=$(PGPASSWORD=miningcore123 psql -h localhost -U miningcore -d miningcore -t -c "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public';" | xargs)

if [ "$ACTUAL_TABLES" -eq "$EXPECTED_TABLES" ]; then
    echo "✅ Database schema: All $EXPECTED_TABLES tables created successfully"
    # List the created tables
    echo "📋 Created tables:"
    PGPASSWORD=miningcore123 psql -h localhost -U miningcore -d miningcore -c "\dt" | grep -E '^ [a-z_]+' | awk '{print "   • " $3}'
else
    echo "❌ Database schema incomplete: Found $ACTUAL_TABLES tables, expected $EXPECTED_TABLES"
    echo "Check the createdb.sql import output above for errors"
    exit 1
fi

# Test database connection
echo "🔗 Testing database connection..."
PGPASSWORD=miningcore123 psql -h localhost -U miningcore -d miningcore -c "SELECT 'Database connection successful!' as status;" | grep successful && echo "✅ Database connection verified" || {
    echo "❌ Database connection failed"
    exit 1
}
```

<a id="miningcore-pool-server-setup"></a>
## Miningcore Pool Server Setup

### 1. Build Miningcore
```bash
cd unicity-mining-core

# Make build scripts executable
chmod +x build-ubuntu-20.04.sh build-ubuntu-22.04.sh

# IMPORTANT: Fix .NET installation path (if not done earlier)
sudo cp -r /usr/lib/dotnet/* /usr/share/dotnet/ 2>/dev/null || true

# Build for Ubuntu 20.04
./build-ubuntu-20.04.sh

# Or build for Ubuntu 22.04
./build-ubuntu-22.04.sh

# Or manual build
cd src/Miningcore
dotnet publish -c Release --framework net6.0 -o ../../build
cd ../..

# Verify build completed successfully
ls -la build/

# Verify critical components are present
if [ ! -f "build/Miningcore" ] || [ ! -f "build/Miningcore.dll" ]; then
    echo "❌ Build failed: Missing critical components (Miningcore executable)"
    echo "Check build output above for errors"
    exit 1
fi

if [ ! -f "build/librandomx.so" ]; then
    echo "❌ Build failed: Missing RandomX library (librandomx.so)"
    echo "RandomX compilation failed - check dependencies"
    exit 1
fi

echo "✅ Build verification passed - all critical components present"
echo "   • Miningcore executable: $(stat -c%s build/Miningcore) bytes" 
echo "   • Miningcore library: $(stat -c%s build/Miningcore.dll) bytes"
echo "   • RandomX library: $(stat -c%s build/librandomx.so) bytes"
```

### 2. Generate Admin API Key

The pool requires a secure API key for admin operations and PaymentProcessor access:

```bash
# Generate a secure 64-character API key
openssl rand -hex 32
```

Example output: `a1b2c3d4e5f6789012345678901234567890abcdef1234567890abcdef123456`

**Save this API key** - you'll need it for the config.json file below and for the PaymentProcessor configuration.

### 3. Create Pool Configuration

Create `config.json` in the root directory:

```json
{
    "logging": {
        "level": "info",
        "enableConsoleLog": true,
        "enableConsoleColors": true,
        "logFile": "alpha-pool.log",
        "apiLogFile": "alpha-api.log",
        "logBaseDirectory": "logs",
        "perPoolLogFile": true
    },
    "banning": {
        "manager": "Integrated",
        "banOnJunkReceive": true,
        "banOnInvalidShares": false
    },
    "notifications": {
        "enabled": false,
        "email": {
            "host": "smtp.example.com",
            "port": 587,
            "user": "user",
            "password": "password",
            "fromAddress": "alpha-pool@unicity-pool.com",
            "fromName": "Alpha Pool"
        },
        "admin": {
            "enabled": false,
            "emailAddress": "admin@example.com",
            "notifyBlockFound": true
        }
    },
    "persistence": {
        "postgres": {
            "host": "127.0.0.1",
            "port": 5432,
            "user": "miningcore",
            "password": "miningcore123",
            "database": "miningcore"
        }
    },
    "paymentProcessing": {
        "enabled": true,
        "interval": 600,
        "shareRecoveryFile": "recovered-shares.txt"
    },
    "api": {
        "enabled": true,
        "listenAddress": "127.0.0.1",
        "port": 4000,
        "adminApiKeys": ["your_admin_api_key_here"],
        "adminIpWhitelist": ["127.0.0.1"],
        "metricsIpWhitelist": [],
        "rateLimiting": {
            "disabled": false,
            "rules": [
                {
                    "Endpoint": "*",
                    "Period": "1s",
                    "Limit": 5
                }
            ],
            "ipWhitelist": ["0.0.0.0/0"]
        }
    },
    "pools": [
        {
            "id": "alpha1",
            "enabled": true,
            "coin": "alpha",
            "address": "your_pool_alpha_address_here",
            "rewardRecipients": [
                {
                    "address": "your_pool_alpha_address_here",
                    "percentage": 5.0
                }
            ],
            "blockRefreshInterval": 300,
            "jobRebroadcastTimeout": 45,
            "clientConnectionTimeout": 600,
            "banning": {
                "enabled": true,
                "time": 600,
                "invalidPercent": 50,
                "checkThreshold": 50
            },
            "AddressType": "BechSegwit",
            "extra": {
                "useSingleInputUtxo": true,
                "changeAddress": "your_pool_alpha_address_here",
                "maxOutputsPerTx": 50
            },
            "ports": {
                "3052": {
                    "listenAddress": "0.0.0.0",
                    "difficulty": 0.1,
                    "tls": false,
                    "varDiff": {
                        "minDiff": 0.01,
                        "maxDiff": null,
                        "targetTime": 30,
                        "retargetTime": 90,
                        "variancePercent": 30,
                        "maxDelta": 500
                    }
                },
                "3053": {
                    "listenAddress": "0.0.0.0",
                    "difficulty": 0.05,
                    "tls": false,
                    "varDiff": {
                        "minDiff": 0.05,
                        "maxDiff": null,
                        "targetTime": 120,
                        "retargetTime": 300,
                        "variancePercent": 30,
                        "maxDelta": 500
                    }
                },
                "3054": {
                    "listenAddress": "0.0.0.0",
                    "difficulty": 0.01,
                    "tls": false,
                    "varDiff": {
                        "minDiff": 0.005,
                        "maxDiff": null,
                        "targetTime": 120,
                        "retargetTime": 300,
                        "variancePercent": 30,
                        "maxDelta": 500
                    }
                }
            },
            "daemons": [
                {
                    "host": "127.0.0.1",
                    "port": 8589,
                    "user": "your_rpc_username",
                    "password": "your_strong_rpc_password",
                    "type": "json-rpc",
                    "zmqBlockNotifySocket": "tcp://127.0.0.1:15101"
                }
            ],
            "paymentProcessing": {
                "enabled": true,
                "minimumPayment": 1,
                "payoutScheme": "PROP",
                "payoutSchemeConfig": {
                }
            }
        }
    ]
}
```

#### Configuration Parameters Explained

**Replace the following placeholder values with your actual configuration:**

- `miningcore123` - PostgreSQL database password (use the simple password from database setup)
- `your_admin_api_key_here` - The API key generated in step 2 above
- `your_pool_alpha_address_here` - Alpha address where pool fees will be collected
- `your_rpc_username` / `your_strong_rpc_password` - Alpha daemon RPC credentials from your Alpha node configuration

**Key Configuration Settings:**

- **paymentProcessing.enabled: true** - Automatic payments enabled (can be disabled if using external PaymentProcessor)
- **payoutScheme: "PROP"** - Proportional payment scheme (fair distribution based on submitted shares)
- **AddressType: "BechSegwit"** - Required for Alpha Bech32 addresses (alpha1... format)
- **rewardRecipients** - Pool fee configuration (5% pool fee in example):
  ```json
  [{"address": "your_pool_alpha_address_here", "percentage": 10.0}]
  ```
- **minimumPayment: 1** - Minimum payout amount in ALPHA before payment is sent to miners
- **extra** - Advanced transaction settings:
  - **useSingleInputUtxo: true** - Optimize transaction creation
  - **changeAddress** - Where transaction change is sent (use pool address)
  - **maxOutputsPerTx: 50** - Maximum payments per transaction
- **ports** - Multiple Stratum mining ports with different difficulties:
  - **3052**: High difficulty (0.1) for powerful miners (minDiff: 0.01)
  - **3053**: Medium difficulty (0.002) for average miners (minDiff: 0.05)
  - **3054**: Lower difficulty (0.01) for weaker miners (minDiff: 0.005, slower timing)
- **varDiff** - Variable difficulty adjustment settings:
  - **targetTime**: Target seconds between shares (30s for ports 3052/3053, 120s for 3054)
  - **retargetTime**: Difficulty adjustment interval (90s for fast ports, 300s for slow)
  - **variancePercent**: 30% - Adjustment trigger threshold
  - **maxDelta**: 500% - Maximum difficulty change per adjustment
- **API security**:
  - **listenAddress: "127.0.0.1"** - Localhost only (more secure than 0.0.0.0)
  - **adminIpWhitelist** - Restrict admin API access to specific IPs
  - **rateLimiting** - Prevent API abuse (5 requests per second)
- **ZMQ Configuration**: 
  - **zmqBlockNotifySocket: "tcp://127.0.0.1:15101"** - Alpha node block notifications
- **Performance settings**:
  - **blockRefreshInterval: 300** - Check for new blocks every 300ms (faster response)
  - **jobRebroadcastTimeout: 45** - Rebroadcast jobs after 45s (improved reliability)

For complete configuration reference and advanced options, see the [Miningcore Configuration Wiki](https://github.com/coinfoundry/miningcore/wiki/Configuration).

### 4. Validate Pool Address

**Critical**: Ensure your pool address is valid for the Alpha network before starting the pool.

```bash
# Generate a new address if needed
./alpha-cli -rpcuser=your_rpc_username -rpcpassword=your_strong_rpc_password getnewaddress

# Validate your pool address format (must be valid Alpha bech32 format)
./alpha-cli -rpcuser=your_rpc_username -rpcpassword=your_strong_rpc_password validateaddress your_pool_address

# Example valid Alpha address format: alpha1q...
# Example output should show: "isvalid": true

# Update your config.json with the validated address
nano config.json
# Replace "your_pool_alpha_address_here" with your validated address
```

### 5. Pre-Launch Configuration Validation

Before starting the pool, validate your configuration to prevent common startup issues:

```bash
echo "🔧 Validating pool configuration before launch..."

# Check Alpha node connectivity
echo "📡 Testing Alpha node RPC connection..."
RPC_USER=$(grep '"user":' ../config.json | cut -d'"' -f4)
RPC_PASS=$(grep '"password":' ../config.json | cut -d'"' -f4)

if curl -s --fail -u "$RPC_USER:$RPC_PASS" \
   -H "Content-Type: application/json" \
   -d '{"jsonrpc":"1.0","id":"test","method":"getblockchaininfo","params":[]}' \
   http://localhost:8589/ > /dev/null; then
    echo "✅ Alpha node RPC connection successful"
    BLOCK_HEIGHT=$(curl -s -u "$RPC_USER:$RPC_PASS" \
                  -H "Content-Type: application/json" \
                  -d '{"jsonrpc":"1.0","id":"test","method":"getblockchaininfo","params":[]}' \
                  http://localhost:8589/ | grep -o '"blocks":[0-9]*' | cut -d':' -f2)
    echo "   📈 Current block height: $BLOCK_HEIGHT"
else
    echo "❌ Cannot connect to Alpha node on localhost:8589"
    echo "   • Check that Alpha node is running"
    echo "   • Verify RPC credentials in config.json match Alpha node configuration"
    echo "   • Ensure RPC port 8589 is accessible"
    exit 1
fi

# Validate pool addresses
echo ""
echo "📍 Validating pool addresses..."
POOL_ADDRESS=$(grep '"address":' ../config.json | head -1 | cut -d'"' -f4)

if [[ "$POOL_ADDRESS" =~ ^alpha1[a-zA-Z0-9]{39}$ ]]; then
    echo "✅ Pool address format valid: $POOL_ADDRESS"
    
    # Test address validation with Alpha node
    if curl -s --fail -u "$RPC_USER:$RPC_PASS" \
       -H "Content-Type: application/json" \
       -d "{\"jsonrpc\":\"1.0\",\"id\":\"test\",\"method\":\"validateaddress\",\"params\":[\"$POOL_ADDRESS\"]}" \
       http://localhost:8589/ | grep '"isvalid":true' > /dev/null; then
        echo "   ✅ Address validated by Alpha node"
    else
        echo "   ⚠️  WARNING: Alpha node validation failed - check address"
    fi
else
    echo "❌ Invalid pool address format: $POOL_ADDRESS"
    echo "   Expected format: alpha1... (40 character Bech32 format)"
    exit 1
fi

# Check database connectivity
echo ""
echo "🗄️  Testing database connection..."
DB_PASS=$(grep '"password":' ../config.json | grep postgres | cut -d'"' -f4)
if PGPASSWORD="$DB_PASS" psql -h localhost -U miningcore -d miningcore -c "SELECT 1;" > /dev/null 2>&1; then
    echo "✅ Database connection successful"
else
    echo "❌ Database connection failed"
    echo "   • Check PostgreSQL is running: sudo systemctl status postgresql"
    echo "   • Verify database credentials in config.json"
    echo "   • Test manual connection: PGPASSWORD=miningcore123 psql -h localhost -U miningcore -d miningcore"
    exit 1
fi

# Validate API configuration
echo ""
echo "🔑 Validating API configuration..."
API_KEY=$(grep '"adminApiKeys":' ../config.json -A1 | grep '"' | cut -d'"' -f2 | head -1)

if [[ ${#API_KEY} -eq 64 ]]; then
    echo "✅ Admin API key format valid (64 characters)"
else
    echo "❌ Admin API key invalid - should be 64 characters"
    echo "   Generate a new key with: openssl rand -hex 32"
    exit 1
fi

echo ""
echo "✅ Configuration validation complete - ready to launch pool! 🚀"
```

### 6. Test Pool Server
```bash
cd build
./Miningcore -c ../config.json
```

### 7. Verify Successful Startup

**Look for these success indicators in the pool startup logs:**

```bash
echo "🚀 Starting pool server..."
start_time=$(date +%s)

# Monitor pool logs for successful startup
tail -f ../logs/pool.log &
LOG_PID=$!

# Give pool time to start and check startup status
sleep 10

echo ""
echo "🔍 Checking pool startup status..."

# Check if pool process is running
if pgrep -f "Miningcore" > /dev/null; then
    echo "✅ Pool process is running"
else
    echo "❌ Pool process not found - check logs above for errors"
    kill $LOG_PID 2>/dev/null || true
    exit 1
fi

# Test API endpoint
echo "🌐 Testing API connectivity..."
if curl -s --fail http://localhost:4000/api/pools > /dev/null; then
    echo "✅ Pool API is responding on port 4000"
    
    # Get pool info
    POOL_INFO=$(curl -s http://localhost:4000/api/pools | grep -o '"id":"[^"]*' | cut -d'"' -f4)
    echo "   📋 Pool ID: $POOL_INFO"
else
    echo "❌ Pool API not accessible on port 4000"
    echo "   Check firewall and pool configuration"
fi

# Test stratum ports
echo "⛏️  Testing mining ports..."
STRATUM_PORTS="3052 3053 3054"
for port in $STRATUM_PORTS; do
    if timeout 5 bash -c "echo > /dev/tcp/localhost/$port" 2>/dev/null; then
        echo "   ✅ Stratum port $port is listening"
    else
        echo "   ❌ Stratum port $port not accessible"
    fi
done

# Check RandomX performance
echo "🔧 Checking RandomX multi-core optimization..."
CPU_CORES=$(nproc)
if grep -q "Creating.*VMs" ../logs/pool.log 2>/dev/null; then
    VM_COUNT=$(grep "Creating.*VMs" ../logs/pool.log | tail -1 | grep -o '[0-9]*' | head -1)
    echo "✅ RandomX initialized with $VM_COUNT VMs (CPU cores: $CPU_CORES)"
    
    if [ "$VM_COUNT" -gt 1 ]; then
        echo "   🚀 Multi-core optimization ACTIVE"
    else
        echo "   ⚠️  WARNING: Only 1 VM created - multi-core optimization may not be active"
    fi
else
    echo "⚠️  RandomX VM information not found in logs yet"
fi

# Stop log monitoring
kill $LOG_PID 2>/dev/null || true

# Calculate startup time
end_time=$(date +%s)
startup_duration=$((end_time - start_time))

echo ""
echo "🎉 Pool startup verification complete in ${startup_duration} seconds!"
echo ""
echo "📋 Pool Status Summary:"
echo "   • Process Status: Running"
echo "   • API Endpoint: http://localhost:4000/api/pools" 
echo "   • Mining Ports: 3052 (high diff), 3053 (med diff), 3054 (low diff)"
echo "   • RandomX: Multi-core optimization enabled"
echo ""
echo "🔗 Next Steps:"
echo "   1. Test with mining software: Connect miner to localhost:3052"
echo "   2. Monitor performance: htop (should show multi-core activity)"
echo "   3. Check logs: tail -f ../logs/pool.log"
echo "   4. Web interface: Set up frontend (next section)"
```

<a id="web-frontend-setup"></a>
## Web Frontend Setup

The mining pool requires a web frontend for miners to view statistics, earnings, and pool information. The frontend is maintained in a separate repository and communicates with the Miningcore API.

### 1. Frontend Repository

**Clone the frontend repository:**

```bash
git clone https://github.com/unicitynetwork/unicity-mining-core-ui.git
cd unicity-mining-core-ui
```

### 2. Frontend Technology Stack

The Unicity mining pool frontend is a **static HTML/CSS/JavaScript application** that includes:

- **HTML5** - Static web pages
- **Bootstrap** - CSS framework for responsive design  
- **JavaScript** - Client-side functionality and API calls
- **Chart.js** - Statistics visualization
- **Periodic polling** - Regular API updates for statistics

### 3. Configure Frontend

The frontend connects to your pool API automatically. You may need to update the API endpoint in `assets/js/miningcore-ui.js` if your pool is not running on the default port:

```javascript
// Look for API base URL configuration in assets/js/miningcore-ui.js
// Update if your pool API runs on a different host/port
const API_BASE_URL = 'http://localhost:4000';
```

### 4. No Build Process Required

Since this is a static frontend, **no build process is needed**. The files are ready to serve directly from any web server.

### 5. Web Server Setup

**Install Nginx:**

```bash
sudo apt update
sudo apt install nginx
```

**Create Nginx configuration** (`/etc/nginx/sites-available/your-pool-domain.com`):

```nginx
# Server block for HTTP to HTTPS redirection
server {
    listen 80;
    listen [::]:80; # Listen on IPv6 as well
    server_name your-pool-domain.com www.your-pool-domain.com;

    # Redirect all HTTP to HTTPS with a 301 (permanent) redirect
    return 301 https://$host$request_uri;
}

# Server block for HTTPS
server {
    listen 443 ssl; # Enable HTTP/2 for better performance
    listen [::]:443 ssl; # Listen on IPv6 as well
    server_name your-pool-domain.com www.your-pool-domain.com;

    # SSL Configuration - Paths to your Let's Encrypt certificates
    ssl_certificate /etc/letsencrypt/live/your-pool-domain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/your-pool-domain.com/privkey.pem;

    # SSL hardening options from Let's Encrypt's certbot or best practices
    include /etc/letsencrypt/options-ssl-nginx.conf; # Recommended SSL parameters
    ssl_dhparam /etc/letsencrypt/ssl-dhparams.pem;   # Diffie-Hellman parameter for DHE ciphersuites

    # Root directory for your website's static files
    root /var/www/your-pool-domain.com/html;
    index index.html index.htm; # Default files to serve

    # Standard security headers (optional but recommended)
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header X-XSS-Protection "1; mode=block" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;

    # Location block for serving static files (your website)
    location / {
        try_files $uri $uri/ /index.html =404; # Serves index.html for SPA-like behavior if needed, or just 404s
    }

    # Cache static assets for better performance
    location ~* \.(js|css|png|jpg|jpeg|gif|ico|svg|woff|woff2|ttf|eot)$ {
        expires 1y;
        add_header Cache-Control "public, immutable";
    }

    # Location block for the Miningcore API proxy
    # Requests to https://your-pool-domain.com/api/... will be forwarded
    location /api/ {
        # URL of your Miningcore API (running on HTTP, on the same machine)
        proxy_pass http://127.0.0.1:4000/api/;

        # Headers to pass to the backend (Miningcore)
        proxy_set_header Host $host; # Passes the original host header
        proxy_set_header X-Real-IP $remote_addr; # Passes the real client IP
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for; # List of IPs if behind multiple proxies
        proxy_set_header X-Forwarded-Proto $scheme; # Informs backend if original request was http or https

        # Timeout options
        proxy_connect_timeout 60s;
        proxy_send_timeout 60s;
        proxy_read_timeout 60s;
    }


    # Optional: You might want to deny access to hidden files like .htaccess or .git
    location ~ /\. {
        deny all;
    }
}
```

**Enable the site:**

```bash
# Copy frontend files to web directory
sudo mkdir -p /var/www/your-pool-domain.com/html
sudo cp -r /path/to/unicity-mining-core-ui/* /var/www/your-pool-domain.com/html/

# Remove default Nginx site to prevent conflicts
sudo rm -f /etc/nginx/sites-enabled/default

# Enable site
sudo ln -s /etc/nginx/sites-available/your-pool-domain.com /etc/nginx/sites-enabled/

# Update site configuration for default server
sudo sed -i 's/listen 80;/listen 80 default_server;/' /etc/nginx/sites-available/your-pool-domain.com
sudo sed -i 's/listen \[::\]:80;/listen [::]:80 default_server;/' /etc/nginx/sites-available/your-pool-domain.com

# Test configuration and reload
sudo nginx -t
sudo systemctl reload nginx

# Verify mining pool interface is served (should show pool content)
curl -s http://localhost/ | grep -i "mining\|pool\|unicity" || echo "Warning: Pool interface may not be loading correctly"
```

### 6. SSL/HTTPS Setup (Production)

**Important:** The Nginx configuration above assumes SSL certificates are already in place. Set up SSL certificates before enabling the site:

**Install Certbot:**

```bash
sudo apt install certbot python3-certbot-nginx
```

**Get SSL certificate:**

```bash
sudo certbot --nginx -d your-pool-domain.com -d www.your-pool-domain.com
```

**Note:** The Nginx configuration provided already includes:
- HTTP to HTTPS redirection
- SSL certificate paths for Let's Encrypt
- Security headers and SSL hardening
- IPv6 support

After running certbot, the configuration will be ready to use.

### 7. Firewall Configuration

```bash
# Allow HTTP and HTTPS
sudo ufw allow 80/tcp      # HTTP
sudo ufw allow 443/tcp     # HTTPS
```

### 8. Frontend Features

The mining pool frontend includes:

**Public Pages:**

- Pool statistics and hashrate charts
- Network difficulty and block height
- Recent blocks found
- Top miners leaderboard
- Getting started / mining instructions
- Pool fee structure

**Miner Dashboard:**

- Individual miner statistics
- Hashrate charts (1h, 24h, 7d)
- Worker status and performance
- Payment history
- Current earnings
- Payment threshold settings

**API Integration:**

- Periodic statistics updates via API polling
- RESTful API for historical data
- Payment notifications
- Block notifications

### 9. Customization

**Branding:**

- Update pool name and logo
- Customize color scheme
- Add pool-specific information
- Configure social media links

**Alpha-Specific Features:**

- Display Alpha network information
- Show Unicity ecosystem links
- Custom mining guides for Alpha
- Alpha-specific pool statistics

### 10. Testing Frontend

```bash
# Test API connectivity
curl https://your-pool-domain.com/api/pools

# Test frontend access
curl -I https://your-pool-domain.com
```

<a id="paymentprocessor-setup-separate-machine-any-os"></a>
## PaymentProcessor Setup (Separate Machine)

**IMPORTANT**: For security, the PaymentProcessor should be deployed on a separate, secured machine from the pool server. This machine can run **Windows, macOS, or Linux** - any OS that supports .NET (6.0 or later).

The payment machine will:
- Host the Unicity Alpha wallet with pool funds  
- Run the PaymentProcessor application
- Connect remotely to the pool server API to fetch pending payments
- Process actual Unicity Alpha blockchain transactions

### 1. Payment Machine Prerequisites

On your payment processing machine (Windows/Mac/Linux):

**Install .NET SDK (6.0 or later):**
- **Windows/Mac**: Download from https://dotnet.microsoft.com/download
- **Linux**: 
  ```bash
  wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
  sudo dpkg -i packages-microsoft-prod.deb
  sudo apt update
  sudo apt install -y dotnet-sdk-6.0
  ```
  
**Note**: PaymentProcessor is compatible with .NET 6.0 and later versions, giving you flexibility in your runtime choice.

**Get the code:**
```bash
git clone https://github.com/yourusername/unicity-mining-core.git
cd unicity-mining-core
```

### 2. Alpha Node Setup on Payment Machine

Install Alpha node from https://github.com/unicitynetwork/alpha on your payment machine as described above and configure with wallet functionality:

```ini
# Alpha node configuration for payment machine
# Required RPC settings
rpcuser=payment_rpc_username
rpcpassword=payment_rpc_strong_password
rpcport=8589
rpcbind=127.0.0.1
rpcallowip=127.0.0.1

# Required mining settings
server=1
daemon=1
txindex=1

# Wallet settings (REQUIRED on payment machine)
wallet=pool_wallet
```

Create and fund the pool wallet:

```bash
# Ensure alpha-cli is in your PATH or use full path to the binary
# Example: /path/to/alpha/bin/alpha-cli

# Create wallet
alpha-cli createwallet "pool_wallet"

# Generate pool payout address  
alpha-cli getnewaddress "pool_payments"

# When the mining pool wins blocks funds will arrive in this address 

```

### 3. Configure PaymentProcessor

Create `src/PaymentProcessor/appsettings.json`:

```json
{
  "PaymentProcessor": {
    "ApiBaseUrl": "https://your-pool-domain.com",
    "PoolId": "alpha1",
    "ApiKey": "your_admin_api_key_here",
    "TimeoutSeconds": 30,
    "AlphaDaemon": {
      "RpcUrl": "http://localhost:8589",
      "RpcUser": "your_rpc_username",
      "RpcPassword": "your_strong_rpc_password",
      "RpcTimeoutSeconds": 30,
      "DataDir": "/path/to/alpha/data",
      "WalletName": "pool_wallet",
      "WalletAddress": "your_pool_wallet_address_here",
      "ChangeAddress": "your_change_address_here",
      "WalletPassword": "",
      "FeePerByte": 0.00001,
      "ConfirmationsRequired": 1,
      "UseWalletRPC": true
    },
    "Automation": {
      "Enabled": false,
      "BatchSize": 10,
      "BlockPeriod": 1,
      "ShowWalletBalance": true,
      "PollingIntervalSeconds": 30,
      "MinimumBalance": 1.0
    }
  },
  "Serilog": {
    "Using": ["Serilog.Sinks.File", "Serilog.Sinks.Console"],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System.Net.Http": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "File",
        "Args": {
          "path": "logs/payment-processor-.log",
          "rollingInterval": "Day",
          "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact",
          "retainedFileCountLimit": 30
        }
      }
    ],
    "Enrich": ["FromLogContext", "WithMachineName", "WithThreadId"],
    "Properties": {
      "Application": "PaymentProcessor"
    }
  }
}
```

#### PaymentProcessor Configuration Parameters

**Replace the following placeholder values with your actual configuration:**

- `your_admin_api_key_here` - The same API key generated for the pool server
- `your_rpc_username` / `your_strong_rpc_password` - Alpha daemon RPC credentials on payment machine  
- `your_pool_wallet_address_here` - Main wallet address for pool payments
- `your_change_address_here` - Alpha address for transaction change (recommend using pool address)
- `your-pool-domain.com` - Your pool's domain name or IP address
- `/path/to/alpha/data` - Path to Alpha blockchain data directory

**PaymentProcessor Settings:**

- **ApiBaseUrl** - Pool server API URL (use HTTPS in production, e.g., `https://your-pool-domain.com`)
- **PoolId** - Must match the pool ID from config.json ("alpha1")
- **TimeoutSeconds** - API request timeout (30s recommended)

**Alpha Daemon Settings:**

- **RpcUrl** - Alpha node RPC endpoint (usually localhost:8589)
- **DataDir** - Alpha blockchain data directory path (e.g., `/opt/alpha` or `~/.alpha`)
- **WalletName** - Name of the pool wallet file
- **WalletAddress** - Pool wallet address for payments (must be specified)
- **ChangeAddress** - Alpha address for transaction change (recommend using pool address)
- **FeePerByte** - Transaction fee rate (0.00001 recommended)
- **ConfirmationsRequired** - Block confirmations before processing (1 for fast payments)
- **UseWalletRPC** - Use Alpha wallet RPC (true recommended)

**Automation Settings:**

- **Enabled: false** - Manual payment processing (set to true for automatic payments)
- **BatchSize: 10** - Number of payments per transaction (10-50 recommended)
- **BlockPeriod: 1** - Process payments every N blocks (1 = every block)
- **ShowWalletBalance: true** - Display wallet balance in logs
- **PollingIntervalSeconds: 30** - How often to check for pending payments
- **MinimumBalance: 1.0** - Minimum wallet balance required before processing payments

**Logging Settings:**

- **MinimumLevel** - Log verbosity (Information = standard, Debug for troubleshooting)
- **WriteTo.File** - Daily rotating log files in logs/ directory
- **retainedFileCountLimit** - Keep 30 days of log files for debugging

### 4. Build and Run PaymentProcessor
```bash
cd src/PaymentProcessor
dotnet build
dotnet run
```

<a id="summary"></a>
## Summary

This guide walks you through setting up a complete, production-ready Unicity Alpha mining pool with the following architecture:

### System Overview

**Pool Server (Linux):**

- **Unicity Alpha Node** - Blockchain synchronization and RPC interface
- **PostgreSQL Database** - Pool statistics, shares, and payment tracking
- **Miningcore Server** - Mining pool core with Stratum protocol (ports 3052-3054)
- **Web Frontend** - Static HTML/CSS/JS interface for miners
- **Nginx Web Server** - HTTPS, security headers, API proxy

**Payment Machine (Any OS):**
- **Alpha Wallet** - Secure fund storage and transaction signing
- **PaymentProcessor** - .NET application for automated miner payouts

### Key Features Implemented

- **Multi-port Stratum mining** with variable difficulty adjustment
- **PROP payment scheme** for fair reward distribution
- **Regular statistics updates** via API polling
- **Secure API** with key-based authentication
- **Production security** with HTTPS, security headers, and firewall rules
- **Automated payments** processed from separate secured machine
- **IPv6 support** and modern web standards

### Security Architecture

- **Wallet isolation** - Payment processing on separate machine
- **API authentication** - Admin operations require API keys
- **Network security** - Firewall rules and localhost-only RPC access
- **HTTPS encryption** - Let's Encrypt SSL certificates
- **Security headers** - XSS protection, frame options, content type validation

### What You've Accomplished

After following this guide, you will have:

1. **Fully operational mining pool** accepting Unicity Alpha miners
2. **Professional web interface** for miner statistics and pool information
3. **Automated payment system** with secure wallet management
4. **Production-grade security** with HTTPS and proper access controls
5. **Scalable architecture** ready for high-traffic mining operations

### Next Steps

- **Test mining connections** using mining software
- **Monitor pool performance** through logs and web interface
- **Configure pool fees** and reward recipients as needed
- **Set up monitoring** and backup procedures for production use
- **Scale resources** based on miner adoption and network hashrate

Your Unicity Alpha mining pool is now ready to serve miners and contribute to the Unicity network security!



<a id="troubleshooting"></a>
## Troubleshooting

### Log Locations
- Pool logs: `logs/pool.log`
- Payment logs: `src/PaymentProcessor/logs/`
- Alpha node logs: `/opt/alpha/debug.log`
- System logs: `journalctl -u miningcore` or `journalctl -u alpha`


<a id="security-best-practices"></a>
## Security Best Practices

### Create Dedicated Pool User

**Important**: Do not run the pool as root in production. Create a dedicated user:

```bash
# Create dedicated user
sudo adduser --disabled-password --gecos "Mining Pool" pooluser
sudo usermod -aG sudo pooluser

# Set ownership of pool files
sudo chown -R pooluser:pooluser /path/to/unicity-mining-core

# Create systemd service for automatic startup
sudo nano /etc/systemd/system/miningcore.service
```

**Systemd service file content:**
```ini
[Unit]
Description=Miningcore Mining Pool
After=network.target postgresql.service

[Service]
Type=simple
User=pooluser
WorkingDirectory=/home/pooluser/unicity-mining-core
ExecStart=/home/pooluser/unicity-mining-core/build/Miningcore -c config.json
Restart=always
RestartSec=10
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
```

```bash
# Enable and start service
sudo systemctl enable miningcore
sudo systemctl start miningcore
sudo systemctl status miningcore

# View logs
sudo journalctl -u miningcore -f
```

### Additional Security Measures

1. **Keep software updated** - Regular system and software updates
2. **Use strong passwords and API keys** - Generate secure 64-character API keys
3. **Implement proper firewall rules** - Only allow necessary ports (22, 4000, 3052-3054)
4. **Regular security audits** - Monitor logs and system access
5. **Backup procedures** - Regular database and configuration backups
6. **Monitor for suspicious activity** - Watch for unusual mining patterns
7. **Use SSL/TLS in production** - Encrypt API and web frontend communications
8. **Limit RPC access to localhost only** - Keep Alpha node RPC internal



---

This guide provides a comprehensive foundation for setting up a Unicity Alpha mining pool. Adjust configurations based on your specific requirements and always test thoroughly before production deployment.