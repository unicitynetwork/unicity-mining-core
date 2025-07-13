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
    private readonly IPaymentCompletionTracker _completionTracker;

    public AlphaPaymentService(
        IAlphaRpcClient rpcClient,
        IPaymentApiClient apiClient,
        PaymentProcessorConfig config, 
        ILogger<AlphaPaymentService> logger,
        IPaymentCompletionTracker completionTracker)
    {
        _rpcClient = rpcClient;
        _apiClient = apiClient;
        _config = config;
        _logger = logger;
        _completionTracker = completionTracker;
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

            // 1. Validate all payments and check already-paid amounts
            _logger.LogInformation("Step 1: Validating payments and checking blockchain history...");
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

            // Check which payments are already completed
            var completedPayments = new List<PendingPayment>();
            foreach (var payment in payments)
            {
                var isCompleted = await _completionTracker.IsPaymentCompletedAsync(payment.Id, cancellationToken);
                
                if (isCompleted)
                {
                    var existingTxId = await _completionTracker.GetPaymentTransactionIdAsync(payment.Id, cancellationToken);
                    _logger.LogInformation("✓ Payment ID {Id} already completed in transaction {TxId}", 
                        payment.Id, existingTxId);
                    
                    // Payment already complete - mark as success and skip processing
                    results.Add(new PaymentProcessingResult
                    {
                        Address = payment.Address,
                        Amount = payment.Amount,
                        Success = true,
                        TransactionId = existingTxId ?? "ALREADY_COMPLETED",
                        IsPartialPayment = false,
                        CompletedAmount = payment.Amount,
                        AllTransactionIds = new List<string> { existingTxId ?? "ALREADY_COMPLETED" }
                    });
                    completedPayments.Add(payment);
                }
                else
                {
                    _logger.LogInformation("Payment ID {Id}: {Address} = {Amount} ALPHA - needs processing", 
                        payment.Id, payment.Address, payment.Amount);
                }
            }
            
            // Filter out already-completed payments
            var pendingPayments = payments.Except(completedPayments).ToList();
            
            if (pendingPayments.Count == 0)
            {
                _logger.LogInformation("All payments already completed. No processing needed.");
                return results;
            }
            
            _logger.LogInformation("Processing {Count} payments that still need completion", pendingPayments.Count);
            payments = pendingPayments;

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
            
            var selectedUtxos = SelectUtxosForBatching(utxos, totalRequired + estimatedFee);
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

            // 6. Create transactions using single-input batching if needed
            _logger.LogInformation("Step 6: Creating and broadcasting transactions...");
            
            if (selectedUtxos.Count == 1)
            {
                // Single UTXO covers all payments - create one transaction
                _logger.LogInformation("Creating single transaction with 1 input and {OutputCount} outputs", outputs.Count);
                var txId = await CreateAndBroadcastTransactionAsync(selectedUtxos, outputs, cancellationToken);
                
                if (txId != null)
                {
                    // 7. Notify mining pool and create success results
                    _logger.LogInformation("Step 7: Notifying mining pool and creating success results...");
                    foreach (var payment in payments)
                    {
                        _logger.LogInformation("✓ Payment completed: ID {Id}, {Address} = {Amount} ALPHA in transaction {TxId}", 
                            payment.Id, payment.Address, payment.Amount, txId);

                        // Mark payment as completed in our tracking system
                        await _completionTracker.MarkPaymentCompletedAsync(payment.Id, txId, cancellationToken);
                        
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
                }
                else
                {
                    _logger.LogError("✗ Failed to create single transaction");
                    return payments.Select(p => new PaymentProcessingResult
                    {
                        Address = p.Address,
                        Amount = p.Amount,
                        Success = false,
                        Error = "Failed to create transaction"
                    }).ToList();
                }
            }
            else
            {
                // Multiple UTXOs - use batching approach (one transaction per UTXO)
                _logger.LogInformation("Using batching approach with {Count} UTXOs", selectedUtxos.Count);
                results = await ProcessPaymentsWithBatchingAsync(payments, selectedUtxos, cancellationToken);
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

    private List<UnspentOutput> SelectUtxosForBatching(List<UnspentOutput> available, decimal required)
    {
        _logger.LogInformation("UTXO Selection: Need {Required} ALPHA from {Count} available UTXOs", required, available.Count);
        
        // Filter to spendable UTXOs with sufficient confirmations
        var spendable = available
            .Where(u => u.Spendable && u.Confirmations >= _config.AlphaDaemon.ConfirmationsRequired)
            .OrderByDescending(u => u.Amount) // Sort by amount descending for efficient selection
            .ToList();

        _logger.LogInformation("Filtered to {Count} spendable UTXOs (min confirmations: {MinConf})", 
            spendable.Count, _config.AlphaDaemon.ConfirmationsRequired);

        if (spendable.Count == 0)
        {
            _logger.LogError("✗ No spendable UTXOs available");
            return new List<UnspentOutput>();
        }

        foreach (var utxo in spendable.Take(10)) // Log first 10 spendable UTXOs
        {
            _logger.LogInformation("Candidate UTXO: {TxId}:{Index} = {Amount} ALPHA", 
                utxo.TxId, utxo.Vout, utxo.Amount);
        }

        // Strategy 1: Try to find a single UTXO that covers the entire amount
        var singleUtxo = spendable.FirstOrDefault(u => u.Amount >= required);
        
        if (singleUtxo != null)
        {
            _logger.LogInformation("✓ Selected single UTXO: {TxId}:{Index} = {Amount} ALPHA (required: {Required})", 
                singleUtxo.TxId, singleUtxo.Vout, singleUtxo.Amount, required);
            return new List<UnspentOutput> { singleUtxo };
        }

        // Strategy 2: Multi-UTXO selection for batching - collect UTXOs that can cover the amount
        _logger.LogInformation("No single UTXO sufficient, selecting UTXOs for batching...");
        
        var selected = new List<UnspentOutput>();
        decimal totalSelected = 0;

        foreach (var utxo in spendable)
        {
            selected.Add(utxo);
            totalSelected += utxo.Amount;
            
            _logger.LogInformation("Added UTXO: {TxId}:{Index} = {Amount} ALPHA (total: {Total})", 
                utxo.TxId, utxo.Vout, utxo.Amount, totalSelected);

            if (totalSelected >= required)
            {
                _logger.LogInformation("✓ UTXO selection for batching: {Count} UTXOs totaling {Total} ALPHA (required: {Required})", 
                    selected.Count, totalSelected, required);
                return selected;
            }
        }

        // Strategy 3: Failed to find sufficient UTXOs
        _logger.LogError("✗ Insufficient UTXOs: need {Required} ALPHA, but only {Available} ALPHA available across {Count} UTXOs", 
            required, totalSelected, spendable.Count);
        
        var largestUtxo = spendable.FirstOrDefault()?.Amount ?? 0;
        _logger.LogInformation("Largest available UTXO: {Amount} ALPHA", largestUtxo);
        _logger.LogInformation("Total available across all UTXOs: {Total} ALPHA", spendable.Sum(u => u.Amount));
        
        return new List<UnspentOutput>();
    }

    private async Task<string?> CreateAndBroadcastTransactionAsync(List<UnspentOutput> utxos, 
        Dictionary<string, decimal> outputs, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Creating raw transaction with {InputCount} inputs and {OutputCount} outputs", 
                utxos.Count, outputs.Count);
            var rawTx = await _rpcClient.CreateRawTransactionAsync(utxos, outputs, cancellationToken);
            _logger.LogInformation("✓ Raw transaction created: {Length} bytes", rawTx.Length);
            
            _logger.LogInformation("Signing transaction...");
            var signedTx = await _rpcClient.SignRawTransactionAsync(rawTx, cancellationToken);
            _logger.LogInformation("✓ Transaction signed successfully");
            
            _logger.LogInformation("Broadcasting transaction to network...");
            var txId = await _rpcClient.SendRawTransactionAsync(signedTx.Hex, cancellationToken);
            _logger.LogInformation("✓ Successfully broadcast transaction: {TxId}", txId);
            
            return txId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create and broadcast transaction");
            return null;
        }
    }

    private async Task<List<PaymentProcessingResult>> ProcessPaymentsWithBatchingAsync(List<PendingPayment> payments, 
        List<UnspentOutput> utxos, CancellationToken cancellationToken)
    {
        var results = new List<PaymentProcessingResult>();
        var successfulTxIds = new List<string>();
        decimal totalCompleted = 0;
        
        // Track cumulative amounts sent to each payment
        var paymentProgress = new Dictionary<long, decimal>();
        foreach (var payment in payments)
        {
            paymentProgress[payment.Id] = 0;
        }
        
        _logger.LogInformation("Starting batched payment processing for {PaymentCount} payments using {UtxoCount} UTXOs", 
            payments.Count, utxos.Count);

        // Process payments using single-input transactions
        foreach (var utxo in utxos)
        {
            try
            {
                var utxoAmount = utxo.Amount;
                var estimatedFee = await EstimateTransactionFeeAsync(payments, cancellationToken);
                var availableForPayments = utxoAmount - estimatedFee;
                
                if (availableForPayments <= 0)
                {
                    _logger.LogWarning("UTXO {TxId}:{Index} too small for payments after fee: {Amount} - {Fee} = {Available}", 
                        utxo.TxId, utxo.Vout, utxoAmount, estimatedFee, availableForPayments);
                    continue;
                }

                // Create outputs for this UTXO's capacity
                var outputs = new Dictionary<string, decimal>();
                decimal amountToDistribute = availableForPayments;
                
                // Find payments that still need completion
                var remainingPayments = new List<PendingPayment>();
                foreach (var payment in payments)
                {
                    var isCompleted = await _completionTracker.IsPaymentCompletedAsync(payment.Id, cancellationToken);
                    if (!isCompleted)
                    {
                        var progressAmount = paymentProgress[payment.Id];
                        var amountStillNeeded = payment.Amount - progressAmount;
                        if (amountStillNeeded > 0.001m) // Only include if significant amount still needed
                        {
                            remainingPayments.Add(payment);
                        }
                    }
                }
                
                if (remainingPayments.Count == 0)
                {
                    _logger.LogInformation("All payments completed, remaining UTXO unused");
                    break;
                }

                // Process the first remaining payment
                var targetPayment = remainingPayments.First();
                var currentProgress = paymentProgress[targetPayment.Id];
                var stillNeeded = targetPayment.Amount - currentProgress;
                var amountToPay = Math.Min(amountToDistribute, stillNeeded);
                
                _logger.LogInformation("Payment {Id}: Required {Required} ALPHA, Already sent {Progress} ALPHA, Still needed {Needed} ALPHA, Processing {Amount} ALPHA with this UTXO", 
                    targetPayment.Id, targetPayment.Amount, currentProgress, stillNeeded, amountToPay);
                
                outputs[targetPayment.Address] = amountToPay;
                
                // Add change if needed
                var change = utxoAmount - amountToPay - estimatedFee;
                if (change > 0.001m)
                {
                    var changeAddress = _config.AlphaDaemon.ChangeAddress;
                    if (string.IsNullOrEmpty(changeAddress))
                    {
                        changeAddress = await _rpcClient.GetNewAddressAsync(cancellationToken);
                    }
                    outputs[changeAddress] = change;
                }

                // Create and broadcast transaction
                var txId = await CreateAndBroadcastTransactionAsync(new List<UnspentOutput> { utxo }, outputs, cancellationToken);
                
                if (txId != null)
                {
                    successfulTxIds.Add(txId);
                    totalCompleted += amountToPay;
                    
                    // Update progress for this payment
                    paymentProgress[targetPayment.Id] += amountToPay;
                    var newProgress = paymentProgress[targetPayment.Id];
                    
                    _logger.LogInformation("✓ Batched payment: {Amount} ALPHA to {Address} in transaction {TxId}", 
                        amountToPay, targetPayment.Address, txId);
                    
                    // Check if payment is now fully completed
                    if (newProgress >= targetPayment.Amount)
                    {
                        // Payment fully completed - mark as completed and notify mining pool
                        await _completionTracker.MarkPaymentCompletedAsync(targetPayment.Id, txId, cancellationToken);
                        
                        _logger.LogInformation("Payment ID {Id} FULLY completed: {Progress}/{Required} ALPHA, notifying mining pool...", 
                            targetPayment.Id, newProgress, targetPayment.Amount);
                        
                        var notified = await _apiClient.MarkPaymentCompletedAsync(_config.PoolId, targetPayment, txId, cancellationToken);
                        
                        // Remove any previous partial results for this payment
                        results.RemoveAll(r => r.Address == targetPayment.Address);
                        
                        results.Add(new PaymentProcessingResult
                        {
                            Address = targetPayment.Address,
                            Amount = targetPayment.Amount,
                            Success = true,
                            TransactionId = txId,
                            IsPartialPayment = false,
                            CompletedAmount = targetPayment.Amount,
                            AllTransactionIds = new List<string> { txId }
                        });
                    }
                    else
                    {
                        // Partial payment - continue processing
                        _logger.LogInformation("Payment ID {Id} partially completed: {Progress}/{Required} ALPHA - continuing...", 
                            targetPayment.Id, newProgress, targetPayment.Amount);
                        
                        // Remove any previous partial results for this payment
                        results.RemoveAll(r => r.Address == targetPayment.Address);
                        
                        results.Add(new PaymentProcessingResult
                        {
                            Address = targetPayment.Address,
                            Amount = targetPayment.Amount,
                            Success = false, // Mark as false until fully completed
                            Error = $"Partial payment: {newProgress}/{targetPayment.Amount} ALPHA completed",
                            TransactionId = txId,
                            IsPartialPayment = true,
                            CompletedAmount = newProgress,
                            AllTransactionIds = new List<string> { txId }
                        });
                    }
                }
                else
                {
                    _logger.LogError("✗ Failed to create transaction for UTXO {TxId}:{Index}", utxo.TxId, utxo.Vout);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing UTXO {TxId}:{Index}", utxo.TxId, utxo.Vout);
            }
        }

        // Log any failed payments
        var totalRequired = payments.Sum(p => p.Amount);
        if (totalCompleted < totalRequired)
        {
            var incompletedPayments = payments.Where(p => 
                !results.Any(r => r.Address == p.Address && r.Success)).ToList();
            
            foreach (var payment in incompletedPayments)
            {
                await LogFailedPaymentAsync(payment, totalCompleted, successfulTxIds, 
                    $"Partial payment processing: {totalCompleted}/{totalRequired} ALPHA completed");
            }
        }

        return results;
    }

    private async Task LogFailedPaymentAsync(PendingPayment payment, decimal completedAmount, 
        List<string> successfulTxIds, string error)
    {
        try
        {
            var logEntry = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} | " +
                           $"FAILED | ID:{payment.Id} | " +
                           $"Address:{payment.Address} | " +
                           $"Required:{payment.Amount} | " +
                           $"Completed:{completedAmount} | " +
                           $"Remaining:{payment.Amount - completedAmount} | " +
                           $"TxIds:{string.Join(",", successfulTxIds)} | " +
                           $"Error:{error}";
            
            await File.AppendAllTextAsync("failed_payments.log", logEntry + Environment.NewLine);
            _logger.LogWarning("Failed payment logged: {LogEntry}", logEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log failed payment for ID {PaymentId}", payment.Id);
        }
    }
}