namespace PaymentProcessor.Configuration;

public class PaymentProcessorConfig
{
    public string ApiBaseUrl { get; set; } = "http://localhost:4000";
    public string PoolId { get; set; } = "alpha";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public AlphaDaemonConfig AlphaDaemon { get; set; } = new();
}

public class AlphaDaemonConfig
{
    public string RpcUrl { get; set; } = "http://localhost:8332";
    public string RpcUser { get; set; } = string.Empty;
    public string RpcPassword { get; set; } = string.Empty;
    public int RpcTimeoutSeconds { get; set; } = 30;
    public string DataDir { get; set; } = string.Empty;
    public string WalletName { get; set; } = string.Empty;
    public string WalletAddress { get; set; } = string.Empty;
    public string ChangeAddress { get; set; } = string.Empty;
    public string WalletPassword { get; set; } = string.Empty;
    public decimal FeePerByte { get; set; } = 0.00001m;
    public int ConfirmationsRequired { get; set; } = 1;
    public bool UseWalletRPC { get; set; } = true;
}