using PaymentProcessor.Models;

namespace PaymentProcessor.Services;

public interface IAlphaRpcClient
{
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<List<string>> ListWalletsAsync(CancellationToken cancellationToken = default);
    Task<WalletInfo> GetWalletInfoAsync(string walletName, CancellationToken cancellationToken = default);
    Task<decimal> GetBalanceAsync(CancellationToken cancellationToken = default);
    Task<decimal> GetBalanceAsync(string walletName, CancellationToken cancellationToken = default);
    Task<List<UnspentOutput>> GetUnspentOutputsAsync(string address, CancellationToken cancellationToken = default);
    Task<List<UnspentOutput>> GetAllUnspentOutputsAsync(CancellationToken cancellationToken = default);
    Task<string> CreateRawTransactionAsync(List<UnspentOutput> inputs, Dictionary<string, decimal> outputs, CancellationToken cancellationToken = default);
    Task<SignedTransaction> SignRawTransactionAsync(string rawTransaction, CancellationToken cancellationToken = default);
    Task<string> SendRawTransactionAsync(string signedTransaction, CancellationToken cancellationToken = default);
    Task<TransactionInfo> GetTransactionAsync(string txId, CancellationToken cancellationToken = default);
    Task<bool> ValidateAddressAsync(string address, CancellationToken cancellationToken = default);
    Task<string> GetNewAddressAsync(CancellationToken cancellationToken = default);
    Task<decimal> GetTotalSentToAddressAsync(string address, CancellationToken cancellationToken = default);
    Task<BlockInfo> GetBlockInfoAsync(string blockHash, CancellationToken cancellationToken = default);
    Task<string> GetBestBlockHashAsync(CancellationToken cancellationToken = default);
    Task<int> GetBlockCountAsync(CancellationToken cancellationToken = default);
    void SetWallet(string walletName);
}