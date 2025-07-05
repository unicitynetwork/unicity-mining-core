using Microsoft.Extensions.Logging;
using PaymentProcessor.Configuration;
using PaymentProcessor.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PaymentProcessor.Services;

public class AlphaRpcClient : IAlphaRpcClient
{
    private readonly HttpClient _httpClient;
    private readonly AlphaDaemonConfig _config;
    private readonly ILogger<AlphaRpcClient> _logger;
    private int _requestId = 0;
    private string? _currentWallet;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AlphaRpcClient(HttpClient httpClient, PaymentProcessorConfig config, ILogger<AlphaRpcClient> logger)
    {
        _httpClient = httpClient;
        _config = config.AlphaDaemon;
        _logger = logger;
        
        SetupHttpClient();
    }

    private void SetupHttpClient()
    {
        _httpClient.BaseAddress = new Uri(_config.RpcUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.RpcTimeoutSeconds);
        
        // Basic authentication
        if (!string.IsNullOrEmpty(_config.RpcUser) && !string.IsNullOrEmpty(_config.RpcPassword))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_config.RpcUser}:{_config.RpcPassword}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "PaymentProcessor/1.0");
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Testing Alpha daemon connection: {RpcUrl}", _config.RpcUrl);
            
            // Use a wallet-agnostic command for connection test
            var endpoint = "";
            var request = new RpcRequest
            {
                Method = "getblockchaininfo",
                Params = new object[0],
                Id = Interlocked.Increment(ref _requestId)
            };

            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("RPC connection failed: {StatusCode} - {Content}", response.StatusCode, errorContent);
                return false;
            }

            _logger.LogInformation("Successfully connected to Alpha daemon");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Alpha daemon");
            return false;
        }
    }

    public async Task<List<string>> ListWalletsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching available wallets");
            var wallets = await ExecuteRpcAsync<List<string>>("listwallets", cancellationToken);
            _logger.LogInformation("Found {Count} wallets", wallets.Count);
            return wallets;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list wallets");
            throw;
        }
    }

    public async Task<WalletInfo> GetWalletInfoAsync(string walletName, CancellationToken cancellationToken = default)
    {
        var previousWallet = _currentWallet;
        try
        {
            SetWallet(walletName);
            
            var balance = await GetBalanceAsync(cancellationToken);
            
            return new WalletInfo
            {
                WalletName = walletName,
                Balance = balance,
                Addresses = new List<string> { "Address available" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not get wallet info for {WalletName}: {Error}", walletName, ex.Message);
            throw; // Don't hide the error, let it fail properly
        }
        finally
        {
            // Restore previous wallet
            if (previousWallet != null)
                SetWallet(previousWallet);
        }
    }

    public async Task<decimal> GetBalanceAsync(string walletName, CancellationToken cancellationToken = default)
    {
        var previousWallet = _currentWallet;
        try
        {
            SetWallet(walletName);
            return await GetBalanceAsync(cancellationToken);
        }
        finally
        {
            if (previousWallet != null)
                SetWallet(previousWallet);
        }
    }

    public void SetWallet(string walletName)
    {
        _currentWallet = walletName;
        _logger.LogDebug("Switched to wallet: {WalletName}", walletName);
    }

    public async Task<decimal> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Getting balance for wallet: {CurrentWallet}", _currentWallet ?? "No wallet set");
            
            var balance = await ExecuteRpcAsync<JsonElement>("getbalance", cancellationToken);
            
            decimal decimalBalance = 0;
            
            if (balance.ValueKind == JsonValueKind.Number)
            {
                if (balance.TryGetDecimal(out decimalBalance))
                {
                    // Success
                }
                else if (balance.TryGetDouble(out var doubleBalance))
                {
                    decimalBalance = (decimal)doubleBalance;
                }
            }
            else if (balance.ValueKind == JsonValueKind.String)
            {
                decimal.TryParse(balance.GetString(), out decimalBalance);
            }
            
            _logger.LogDebug("Wallet balance: {Balance} ALPHA", decimalBalance);
            return decimalBalance;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get wallet balance for wallet: {CurrentWallet}", _currentWallet ?? "No wallet set");
            throw;
        }
    }

    public async Task<List<UnspentOutput>> GetUnspentOutputsAsync(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var utxos = await ExecuteRpcAsync<List<UnspentOutput>>("listunspent", cancellationToken, 1, 999999, new[] { address }) ?? new List<UnspentOutput>();
            _logger.LogInformation("Found {Count} unspent outputs for address {Address}", utxos?.Count ?? 0, address);
            return utxos ?? new List<UnspentOutput>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get unspent outputs for address {Address}", address);
            throw;
        }
    }

    public async Task<List<UnspentOutput>> GetAllUnspentOutputsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var utxos = await ExecuteRpcAsync<List<UnspentOutput>>("listunspent", cancellationToken) ?? new List<UnspentOutput>();
            _logger.LogInformation("Found {Count} unspent outputs for wallet", utxos?.Count ?? 0);
            return utxos ?? new List<UnspentOutput>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get unspent outputs for wallet");
            throw;
        }
    }

    public async Task<string> CreateRawTransactionAsync(List<UnspentOutput> inputs, Dictionary<string, decimal> outputs, CancellationToken cancellationToken = default)
    {
        try
        {
            var inputsArray = inputs.Select(i => new { txid = i.TxId, vout = i.Vout }).ToArray();
            
            // Round amounts to 8 decimal places (satoshi precision) for Alpha daemon compatibility
            var roundedOutputs = outputs.ToDictionary(
                kvp => kvp.Key, 
                kvp => Math.Round(kvp.Value, 8)
            );
            
            var rawTx = await ExecuteRpcAsync<string>("createrawtransaction", cancellationToken, inputsArray, roundedOutputs);
            
            _logger.LogInformation("Created raw transaction with {InputCount} inputs and {OutputCount} outputs", 
                inputs.Count, outputs.Count);
            
            return rawTx;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create raw transaction");
            throw;
        }
    }

    public async Task<SignedTransaction> SignRawTransactionAsync(string rawTransaction, CancellationToken cancellationToken = default)
    {
        try
        {
            SignedTransaction? signedTx;
            
            if (_config.UseWalletRPC)
            {
                // Use wallet to sign
                signedTx = await ExecuteRpcAsync<SignedTransaction>("signrawtransactionwithwallet", cancellationToken, rawTransaction);
            }
            else
            {
                // Sign with specific private keys (would need key management)
                signedTx = await ExecuteRpcAsync<SignedTransaction>("signrawtransactionwithkey", cancellationToken, rawTransaction);
            }
            
            if (signedTx == null || !signedTx.Complete)
            {
                var errors = string.Join(", ", signedTx?.Errors?.Select(e => e.Error) ?? new[] { "Unknown error" });
                throw new InvalidOperationException($"Failed to sign transaction: {errors}");
            }
            
            _logger.LogInformation("Successfully signed transaction");
            return signedTx;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sign raw transaction");
            throw;
        }
    }

    public async Task<string> SendRawTransactionAsync(string signedTransaction, CancellationToken cancellationToken = default)
    {
        try
        {
            var txId = await ExecuteRpcAsync<string>("sendrawtransaction", cancellationToken, signedTransaction);
            _logger.LogInformation("Successfully broadcast transaction: {TxId}", txId);
            return txId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send raw transaction");
            throw;
        }
    }

    public async Task<TransactionInfo> GetTransactionAsync(string txId, CancellationToken cancellationToken = default)
    {
        try
        {
            var txInfo = await ExecuteRpcAsync<TransactionInfo>("gettransaction", cancellationToken, txId);
            return txInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get transaction info for {TxId}", txId);
            throw;
        }
    }

    public async Task<bool> ValidateAddressAsync(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ExecuteRpcAsync<object>("validateaddress", cancellationToken, address);
            
            if (result is JsonElement jsonElement)
            {
                if (jsonElement.TryGetProperty("isvalid", out var isValidProperty))
                {
                    return isValidProperty.GetBoolean();
                }
            }
            
            _logger.LogWarning("Could not validate address format for {Address}, assuming valid", address);
            return true; // Assume valid if we can't validate
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate address {Address}", address);
            return true; // Assume valid on error to not block payments
        }
    }

    public async Task<string> GetNewAddressAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var address = await ExecuteRpcAsync<string>("getnewaddress", cancellationToken);
            _logger.LogInformation("Generated new address: {Address}", address);
            return address;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate new address");
            throw;
        }
    }

    private async Task<T> ExecuteRpcAsync<T>(string method, CancellationToken cancellationToken, params object[] parameters)
    {
        var request = new RpcRequest
        {
            Method = method,
            Params = parameters,
            Id = Interlocked.Increment(ref _requestId)
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogDebug("RPC Call: {Method} with {ParamCount} parameters on wallet {Wallet}", method, parameters.Length, _currentWallet ?? "default");

        // Use wallet-specific endpoint if wallet is set
        var endpoint = string.IsNullOrEmpty(_currentWallet) ? "" : $"/wallet/{_currentWallet}";
        var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"RPC call failed: {response.StatusCode} - {errorContent}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var rpcResponse = JsonSerializer.Deserialize<RpcResponse<T>>(responseJson, _jsonOptions);

        if (rpcResponse?.Error != null)
        {
            throw new InvalidOperationException($"RPC Error {rpcResponse.Error.Code}: {rpcResponse.Error.Message}");
        }

        return rpcResponse.Result;
    }
}