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

# Install .NET 6 SDK
wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-6.0

# Fix common .NET installation path issue
sudo cp -r /usr/lib/dotnet/* /usr/share/dotnet/ 2>/dev/null || true

# Verify .NET installation
dotnet --version

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
zmqpubhashblock=tcp://127.0.0.1:28332
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

### 2. Configure PostgreSQL
```bash
# Start PostgreSQL service
sudo systemctl start postgresql
sudo systemctl enable postgresql

# Create database user and database
sudo -u postgres psql << EOF
CREATE ROLE miningcore WITH LOGIN ENCRYPTED PASSWORD 'your_postgres_secure_password';
CREATE DATABASE miningcore OWNER miningcore;
GRANT ALL PRIVILEGES ON DATABASE miningcore TO miningcore;
\q
EOF
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
```

<a id="miningcore-pool-server-setup"></a>
## Miningcore Pool Server Setup

### 1. Build Miningcore
```bash
cd unicity-mining-core

# Build for Ubuntu 20.04
./build-ubuntu-20.04.sh

# Or build for Ubuntu 22.04
./build-ubuntu-22.04.sh

# Or manual build
cd src/Miningcore
dotnet publish -c Release --framework net6.0 -o ../../build
cd ../..
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
    "logFile": "logs/pool.log",
    "logBaseDirectory": "logs"
  },
  "banning": {
    "manager": "Integrated",
    "banOnJunkReceive": true,
    "banOnInvalidShares": false
  },
  "notifications": {
    "enabled": false
  },
  "persistence": {
    "postgres": {
      "host": "127.0.0.1",
      "port": 5432,
      "user": "miningcore",
      "password": "your_postgres_secure_password",
      "database": "miningcore"
    }
  },
  "paymentProcessing": {
    "enabled": false,
    "interval": 600
  },
  "api": {
    "enabled": true,
    "listenAddress": "0.0.0.0",
    "port": 4000,
    "adminApiKeys": ["your_admin_api_key_here"]
  },
  "pools": [
    {
      "id": "alpha1",
      "enabled": true,
      "coin": "alpha",
      "address": "your_pool_alpha_address_here",
      "rewardRecipients": [],
      "blockRefreshInterval": 1000,
      "jobRebroadcastTimeout": 10,
      "clientConnectionTimeout": 600,
      "banning": {
        "enabled": true,
        "time": 600,
        "invalidPercent": 50,
        "checkThreshold": 50
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
          "difficulty": 0.002,
          "tls": false,
          "varDiff": {
            "minDiff": 0.05,
            "maxDiff": null,
            "targetTime": 30,
            "retargetTime": 90,
            "variancePercent": 30,
            "maxDelta": 500
          }
        },
        "3054": {
          "listenAddress": "0.0.0.0",
          "difficulty": 0.0002,
          "tls": false,
          "varDiff": {
            "minDiff": 0.00002,
            "maxDiff": null,
            "targetTime": 30,
            "retargetTime": 90,
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
          "password": "your_strong_rpc_password"
        }
      ],
      "paymentProcessing": {
        "enabled": false,
        "minimumPayment": 1.0,
        "payoutScheme": "PROP",
        "payoutSchemeConfig": {
          "factor": 1.0
        }
      }
    }
  ]
}
```

#### Configuration Parameters Explained

**Replace the following placeholder values with your actual configuration:**

- `your_postgres_secure_password` - PostgreSQL database password you created earlier
- `your_admin_api_key_here` - The API key generated in step 2 above
- `your_pool_alpha_address_here` - Alpha address where pool fees will be collected
- `your_rpc_username` / `your_strong_rpc_password` - Alpha daemon RPC credentials from your Alpha node configuration

**Key Configuration Settings:**

- **paymentProcessing.enabled: false** - Automatic payments disabled (using external PaymentProcessor)
- **payoutScheme: "PROP"** - Proportional payment scheme (fair distribution based on submitted shares)
- **rewardRecipients: []** - Pool fee recipients (empty = no pool fees)
  - Example with 2% pool fee: `[{"address": "alpha1q...", "percentage": 2.0}]`
- **minimumPayment: 1.0** - Minimum payout amount in ALPHA before payment is sent to miners
- **factor: 1.0** - PROP scheme factor (1.0 = standard proportional distribution)
- **ports** - Multiple Stratum mining ports with different difficulties:
  - **3052**: High difficulty (0.1) for powerful miners
  - **3053**: Medium difficulty (0.002) for average miners  
  - **3054**: Low difficulty (0.0002) for low-power miners
- **varDiff** - Variable difficulty adjustment for optimal performance:
  - **minDiff**: Minimum difficulty allowed (0.00002 for low-power miners)
  - **maxDiff**: Maximum difficulty (null = no limit, auto-adjusts based on hashrate)
  - **targetTime**: Target time in seconds between shares (30s = optimal for most miners)
  - **retargetTime**: How often to adjust difficulty in seconds (90s intervals)
  - **variancePercent**: Allowed variance from target time before adjustment (30% = adjust if shares come faster than 21s or slower than 39s)
  - **maxDelta**: Maximum percentage difficulty change per adjustment (500% = allows large adjustments for quick convergence)
- **api.port: 4000** - Pool API port for statistics and PaymentProcessor access
- **blockRefreshInterval: 1000** - How often (ms) to check for new blocks

For complete configuration reference and advanced options, see the [Miningcore Configuration Wiki](https://github.com/coinfoundry/miningcore/wiki/Configuration).

### 4. Test Pool Server
```bash
cd build
./Miningcore -c ../config.json
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

# Enable site
sudo ln -s /etc/nginx/sites-available/your-pool-domain.com /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
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
    "ApiBaseUrl": "http://localhost:4000",
    "PoolId": "alpha1",
    "ApiKey": "your_admin_api_key_here",
    "TimeoutSeconds": 30,
    "AlphaDaemon": {
      "RpcUrl": "http://localhost:8589",
      "RpcUser": "your_rpc_username",
      "RpcPassword": "your_strong_rpc_password",
      "RpcTimeoutSeconds": 30,
      "DataDir": "/opt/alpha",
      "WalletName": "pool_wallet",
      "WalletAddress": "",
      "ChangeAddress": "your_change_address_here",
      "WalletPassword": "",
      "FeePerByte": 0.00001,
      "ConfirmationsRequired": 1,
      "UseWalletRPC": true
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
- `your_change_address_here` - Alpha address for transaction change (recommend using pool address)

**PaymentProcessor Settings:**

- **ApiBaseUrl** - Pool server API URL (update to your pool server's IP/domain in production)
- **PoolId** - Must match the pool ID from config.json ("alpha1")
- **TimeoutSeconds** - API request timeout (30s recommended)

**AlphaDaemon Settings:**

- **RpcUrl** - Local Alpha daemon on payment machine (always localhost:8589)
- **DataDir** - Alpha blockchain data directory path
- **WalletName** - Wallet containing pool funds ("pool_wallet")
- **WalletAddress** - Leave empty (auto-detected from wallet)
- **ChangeAddress** - Address for transaction change (prevents address reuse)
- **FeePerByte** - Transaction fee rate (0.00001 ALPHA recommended)
- **ConfirmationsRequired** - UTXOs must have this many confirmations (1 = faster payments)
- **UseWalletRPC** - Always true (use wallet for signing transactions)

**Serilog Settings:**

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

### Common Issues

#### Pool Won't Start
- Check Alpha node is running and synced
- Verify database connection
- Check configuration file syntax
- Review log files in `logs/` directory

#### No Shares Accepted
- Verify stratum port configuration
- Check Alpha node connectivity
- Validate pool address
- Review miner configuration

#### Payment Issues
- Ensure Alpha node wallet has sufficient funds
- Check PaymentProcessor configuration
- Verify API key authentication
- Review payment logs

#### Performance Issues
- Monitor system resources (CPU, RAM, network)
- Check database performance
- Review pool configuration parameters
- Consider scaling up hardware

### Log Locations
- Pool logs: `logs/pool.log`
- Payment logs: `src/PaymentProcessor/logs/`
- Alpha node logs: `/opt/alpha/debug.log`
- System logs: `journalctl -u miningcore` or `journalctl -u alpha`


<a id="security-best-practices"></a>
## Security Best Practices

1. **Keep software updated**
2. **Use strong passwords and API keys**
3. **Implement proper firewall rules**
4. **Regular security audits**
5. **Backup procedures**
6. **Monitor for suspicious activity**
7. **Use SSL/TLS in production**
8. **Limit RPC access to localhost only**



---

This guide provides a comprehensive foundation for setting up a Unicity Alpha mining pool. Adjust configurations based on your specific requirements and always test thoroughly before production deployment.