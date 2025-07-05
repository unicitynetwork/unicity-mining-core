using Microsoft.Extensions.Logging;
using PaymentProcessor.Configuration;
using PaymentProcessor.Models;

namespace PaymentProcessor.Services;

public class AlphaPaymentService : IAlphaPaymentService
{
    private readonly IAlphaRpcClient _rpcClient;
    private readonly IPaymentApiClient _apiClient;
    private readonly PaymentProcessorConfig _config;
    private readonly ILogger<AlphaPaymentService> _logger;

    public AlphaPaymentService(
        IAlphaRpcClient rpcClient,
        IPaymentApiClient apiClient,
        PaymentProcessorConfig config, 
        ILogger<AlphaPaymentService> logger)
    {
        _rpcClient = rpcClient;
        _apiClient = apiClient;
        _config = config;
        _logger = logger;
    }

    public async Task<PaymentProcessingResult> ProcessPaymentAsync(PendingPayment payment, CancellationToken cancellationToken = default)
    {
        var payments = new List<PendingPayment> { payment };
        var results = await ProcessPaymentsBatchAsync(payments, cancellationToken);
        return results.FirstOrDefault() ?? new PaymentProcessingResult
        {
            Address = payment.Address,
            Amount = payment.Amount,
            Success = false,
            Error = "Failed to process payment"
        };
    }

    public async Task<List<PaymentProcessingResult>> ProcessPaymentsBatchAsync(List<PendingPayment> payments, CancellationToken cancellationToken = default)
    {
        var results = new List<PaymentProcessingResult>();

        try
        {
            _logger.LogInformation("=== Processing batch of {Count} payments ===", payments.Count);
            
            // Log all payments in the batch
            foreach (var payment in payments)
            {
                _logger.LogInformation("Payment ID {Id}: {Address} = {Amount} ALPHA", payment.Id, payment.Address, payment.Amount);
            }

            // 1. Validate all payments
            _logger.LogInformation("Step 1: Validating payments...");
            var totalAmount = payments.Sum(p => p.Amount);
            _logger.LogInformation("Total payment amount: {Total} ALPHA", totalAmount);
            var isValid = await ValidatePaymentsAsync(payments, cancellationToken);
            if (!isValid)
            {
                _logger.LogError("✗ Payment validation failed");
                return payments.Select(p => new PaymentProcessingResult
                {
                    Address = p.Address,
                    Amount = p.Amount,
                    Success = false,
                    Error = "Payment validation failed"
                }).ToList();
            }
            _logger.LogInformation("✓ Payment validation successful");

            // 2. Check wallet balance
            _logger.LogInformation("Step 2: Checking wallet balance...");
            var availableBalance = await GetAvailableBalanceAsync(cancellationToken);
            _logger.LogInformation("Current wallet balance: {Balance} ALPHA", availableBalance);
            
            var totalRequired = payments.Sum(p => p.Amount);
            var estimatedFee = await EstimateTransactionFeeAsync(payments, cancellationToken);
            _logger.LogInformation("Estimated transaction fee: {Fee} ALPHA", estimatedFee);
            _logger.LogInformation("Total required: {Total} ALPHA (payments: {Payments} + fee: {Fee})", 
                totalRequired + estimatedFee, totalRequired, estimatedFee);
            
            if (availableBalance < totalRequired + estimatedFee)
            {
                var error = $"Insufficient balance. Required: {totalRequired + estimatedFee}, Available: {availableBalance}";
                _logger.LogError("✗ {Error}", error);
                
                return payments.Select(p => new PaymentProcessingResult
                {
                    Address = p.Address,
                    Amount = p.Amount,
                    Success = false,
                    Error = error
                }).ToList();
            }
            _logger.LogInformation("✓ Sufficient balance available");

            // 3. Get UTXOs for the pool wallet
            _logger.LogInformation("Step 3: Getting UTXOs...");
            var utxos = await _rpcClient.GetAllUnspentOutputsAsync(cancellationToken);
            _logger.LogInformation("Found {Count} UTXOs with total value: {Total} ALPHA", 
                utxos.Count, utxos.Sum(u => u.Amount));
            
            foreach (var utxo in utxos.Take(5)) // Log first 5 UTXOs
            {
                _logger.LogInformation("UTXO: {TxId}:{Index} = {Amount} ALPHA (confirmations: {Confirmations}, spendable: {Spendable})", 
                    utxo.TxId, utxo.Vout, utxo.Amount, utxo.Confirmations, utxo.Spendable);
            }
            
            var selectedUtxos = SelectUtxos(utxos, totalRequired + estimatedFee);
            _logger.LogInformation("Selected {Count} UTXOs for transaction", selectedUtxos.Count);

            if (selectedUtxos.Sum(u => u.Amount) < totalRequired + estimatedFee)
            {
                var error = "Could not select sufficient UTXOs for transaction";
                _logger.LogError("✗ {Error}", error);
                
                return payments.Select(p => new PaymentProcessingResult
                {
                    Address = p.Address,
                    Amount = p.Amount,
                    Success = false,
                    Error = error
                }).ToList();
            }
            _logger.LogInformation("✓ Selected UTXOs have sufficient funds: {Amount} ALPHA", selectedUtxos.Sum(u => u.Amount));

            // 4. Create transaction outputs
            _logger.LogInformation("Step 4: Creating transaction outputs...");
            var outputs = new Dictionary<string, decimal>();
            foreach (var payment in payments)
            {
                if (outputs.ContainsKey(payment.Address))
                {
                    outputs[payment.Address] += payment.Amount;
                    _logger.LogInformation("Added to existing output: {Address} = {Amount} ALPHA (total: {Total})", 
                        payment.Address, payment.Amount, outputs[payment.Address]);
                }
                else
                {
                    outputs[payment.Address] = payment.Amount;
                    _logger.LogInformation("Created output: {Address} = {Amount} ALPHA", 
                        payment.Address, payment.Amount);
                }
            }

            // 5. Add change output if needed
            _logger.LogInformation("Step 5: Calculating change...");
            var totalInput = selectedUtxos.Sum(u => u.Amount);
            var totalOutput = totalRequired;
            var change = totalInput - totalOutput - estimatedFee;
            _logger.LogInformation("Input: {Input} ALPHA, Output: {Output} ALPHA, Fee: {Fee} ALPHA, Change: {Change} ALPHA", 
                totalInput, totalOutput, estimatedFee, change);

            if (change > 0.001m) // Only add change if significant
            {
                var changeAddress = _config.AlphaDaemon.ChangeAddress;
                if (string.IsNullOrEmpty(changeAddress))
                {
                    _logger.LogInformation("No change address configured, generating new address...");
                    changeAddress = await _rpcClient.GetNewAddressAsync(cancellationToken);
                    _logger.LogInformation("Generated new change address: {Address}", changeAddress);
                }
                else
                {
                    _logger.LogInformation("Using configured change address: {Address}", changeAddress);
                }
                
                outputs[changeAddress] = change;
                _logger.LogInformation("Added change output: {Address} = {Amount} ALPHA", changeAddress, change);
            }

            // 6. Create, sign, and broadcast transaction
            _logger.LogInformation("Step 6: Creating and broadcasting transaction...");
            
            _logger.LogInformation("Creating raw transaction with {InputCount} inputs and {OutputCount} outputs", 
                selectedUtxos.Count, outputs.Count);
            var rawTx = await _rpcClient.CreateRawTransactionAsync(selectedUtxos, outputs, cancellationToken);
            _logger.LogInformation("✓ Raw transaction created: {Length} bytes", rawTx.Length);
            
            _logger.LogInformation("Signing transaction...");
            var signedTx = await _rpcClient.SignRawTransactionAsync(rawTx, cancellationToken);
            _logger.LogInformation("✓ Transaction signed successfully");
            
            _logger.LogInformation("Broadcasting transaction to network...");
            var txId = await _rpcClient.SendRawTransactionAsync(signedTx.Hex, cancellationToken);
            _logger.LogInformation("✓ Successfully broadcast transaction: {TxId}", txId);

            // 7. Notify mining pool and create success results
            _logger.LogInformation("Step 7: Notifying mining pool and creating success results...");
            foreach (var payment in payments)
            {
                _logger.LogInformation("✓ Payment completed: ID {Id}, {Address} = {Amount} ALPHA in transaction {TxId}", 
                    payment.Id, payment.Address, payment.Amount, txId);

                // Notify the mining pool that payment is completed
                _logger.LogInformation("Notifying mining pool about completed payment ID {Id}...", payment.Id);
                var notified = await _apiClient.MarkPaymentCompletedAsync(_config.PoolId, payment, txId, cancellationToken);
                
                if (notified)
                {
                    _logger.LogInformation("✓ Successfully notified mining pool about payment ID {Id} completion", payment.Id);
                }
                else
                {
                    _logger.LogWarning("⚠ Failed to notify mining pool about payment ID {Id} completion (payment still processed on blockchain)", payment.Id);
                }
                
                results.Add(new PaymentProcessingResult
                {
                    Address = payment.Address,
                    Amount = payment.Amount,
                    Success = true,
                    TransactionId = txId
                });
            }

            _logger.LogInformation("=== Batch processing completed successfully ===");
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process payment batch");
            
            // Return error results for all payments
            return payments.Select(p => new PaymentProcessingResult
            {
                Address = p.Address,
                Amount = p.Amount,
                Success = false,
                Error = ex.Message
            }).ToList();
        }
    }

    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting available balance from RPC client");
        return await _rpcClient.GetBalanceAsync(cancellationToken);
    }

    public async Task<bool> ValidatePaymentsAsync(List<PendingPayment> payments, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var payment in payments)
            {
                // Validate amount
                if (payment.Amount <= 0)
                {
                    _logger.LogError("Invalid payment amount: {Amount} for address {Address}", payment.Amount, payment.Address);
                    return false;
                }

                // Validate address
                var isValidAddress = await _rpcClient.ValidateAddressAsync(payment.Address, cancellationToken);
                if (!isValidAddress)
                {
                    _logger.LogError("Invalid address: {Address}", payment.Address);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating payments");
            return false;
        }
    }

    public Task<decimal> EstimateTransactionFeeAsync(List<PendingPayment> payments, CancellationToken cancellationToken = default)
    {
        try
        {
            // Simple fee estimation based on number of inputs and outputs
            // In reality, you'd want more sophisticated fee estimation
            
            var outputCount = payments.GroupBy(p => p.Address).Count(); // Unique addresses
            var estimatedInputCount = Math.Max(1, outputCount); // Rough estimate
            
            // Estimate transaction size: 
            // Base: 10 bytes
            // Input: ~150 bytes each
            // Output: ~34 bytes each
            var estimatedSize = 10 + (estimatedInputCount * 150) + (outputCount * 34);
            
            var estimatedFee = estimatedSize * _config.AlphaDaemon.FeePerByte;
            
            _logger.LogDebug("Estimated transaction fee: {Fee} ALPHA for {Inputs} inputs and {Outputs} outputs", 
                estimatedFee, estimatedInputCount, outputCount);
            
            return Task.FromResult(estimatedFee);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to estimate transaction fee");
            return Task.FromResult(0.001m); // Fallback fee
        }
    }

    private List<UnspentOutput> SelectUtxos(List<UnspentOutput> available, decimal required)
    {
        _logger.LogInformation("UTXO Selection: Need {Required} ALPHA from {Count} available UTXOs", required, available.Count);
        
        // Simple UTXO selection: first UTXO that has enough funds
        var spendable = available
            .Where(u => u.Spendable && u.Confirmations >= _config.AlphaDaemon.ConfirmationsRequired)
            .ToList();

        _logger.LogInformation("Filtered to {Count} spendable UTXOs (min confirmations: {MinConf})", 
            spendable.Count, _config.AlphaDaemon.ConfirmationsRequired);

        foreach (var utxo in spendable.Take(10)) // Log first 10 spendable UTXOs
        {
            _logger.LogInformation("Candidate UTXO: {TxId}:{Index} = {Amount} ALPHA", 
                utxo.TxId, utxo.Vout, utxo.Amount);
        }

        var singleUtxo = spendable.FirstOrDefault(u => u.Amount >= required);
        
        if (singleUtxo != null)
        {
            _logger.LogInformation("✓ Selected UTXO: {TxId}:{Index} = {Amount} ALPHA (required: {Required})", 
                singleUtxo.TxId, singleUtxo.Vout, singleUtxo.Amount, required);
            return new List<UnspentOutput> { singleUtxo };
        }

        _logger.LogError("✗ No single UTXO found with sufficient funds (required: {Required} ALPHA)", required);
        _logger.LogInformation("Largest available UTXO: {Amount} ALPHA", spendable.Max(u => u.Amount));
        return new List<UnspentOutput>();
    }
}