namespace PaymentProcessor.Models;

public record UnspentOutput
{
    public string TxId { get; init; } = string.Empty;
    public int Vout { get; init; }
    public decimal Amount { get; init; }
    public int Confirmations { get; init; }
    public string Address { get; init; } = string.Empty;
    public string ScriptPubKey { get; init; } = string.Empty;
    public bool Spendable { get; init; }
    public bool Solvable { get; init; }
}

public record SignedTransaction
{
    public string Hex { get; init; } = string.Empty;
    public bool Complete { get; init; }
    public List<SigningError> Errors { get; init; } = new();
}

public record SigningError
{
    public string TxId { get; init; } = string.Empty;
    public int Vout { get; init; }
    public string ScriptSig { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string Error { get; init; } = string.Empty;
}

public record TransactionInfo
{
    public string TxId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public decimal Fee { get; init; }
    public int Confirmations { get; init; }
    public string BlockHash { get; init; } = string.Empty;
    public DateTime Time { get; init; }
    public List<TransactionInput> Inputs { get; init; } = new();
    public List<TransactionOutput> Outputs { get; init; } = new();
}

public record TransactionInput
{
    public string TxId { get; init; } = string.Empty;
    public int Vout { get; init; }
    public decimal Amount { get; init; }
    public string Address { get; init; } = string.Empty;
}

public record TransactionOutput
{
    public decimal Amount { get; init; }
    public string Address { get; init; } = string.Empty;
    public int N { get; init; }
}

public record RpcRequest
{
    public string Method { get; init; } = string.Empty;
    public object[] Params { get; init; } = Array.Empty<object>();
    public int Id { get; init; }
    public string JsonRpc { get; init; } = "2.0";
}

public record RpcResponse<T>
{
    public T? Result { get; init; }
    public RpcError? Error { get; init; }
    public int Id { get; init; }
}

public record RpcError
{
    public int Code { get; init; }
    public string Message { get; init; } = string.Empty;
    public object? Data { get; init; }
}

public record WalletInfo
{
    public string WalletName { get; init; } = string.Empty;
    public decimal Balance { get; init; }
    public int TxCount { get; init; }
    public decimal UnconfirmedBalance { get; init; }
    public decimal ImmatureBalance { get; init; }
    public bool Unlocked { get; init; }
    public List<string> Addresses { get; init; } = new();
}

public record BlockInfo
{
    public string Hash { get; init; } = string.Empty;
    public int Height { get; init; }
    public DateTime Time { get; init; }
    public string PreviousBlockHash { get; init; } = string.Empty;
    public string NextBlockHash { get; init; } = string.Empty;
    public int TxCount { get; init; }
    public long Size { get; init; }
    public decimal Difficulty { get; init; }
}