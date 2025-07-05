using Microsoft.Extensions.Logging;
using PaymentProcessor.Configuration;
using PaymentProcessor.Models;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PaymentProcessor.Services;

public class PaymentApiClient : IPaymentApiClient
{
    private readonly HttpClient _httpClient;
    private readonly PaymentProcessorConfig _config;
    private readonly ILogger<PaymentApiClient> _logger;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PaymentApiClient(HttpClient httpClient, PaymentProcessorConfig config, ILogger<PaymentApiClient> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        
        _httpClient.BaseAddress = new Uri(_config.ApiBaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
        
        // Add user agent for better API compatibility
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "PaymentProcessor/1.0");
        
        // Add API key authentication if configured
        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_config.ApiKey}");
        }
    }

    public async Task<List<PendingPayment>> GetPendingPaymentsAsync(string poolId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching pending payments for pool: {PoolId}", poolId);
            
            var endpoint = $"/api/admin/pools/{poolId}/payments/pending";
            var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to fetch pending payments. Status: {StatusCode}, Reason: {ReasonPhrase}, Content: {Content}", 
                    response.StatusCode, response.ReasonPhrase, errorContent);
                
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || 
                    response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Admin API access may be restricted. Check IP whitelist or authentication requirements.");
                }
                
                return new List<PendingPayment>();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var response_data = JsonSerializer.Deserialize<PendingPaymentsResponse>(content, _jsonOptions);
            
            _logger.LogInformation("Successfully fetched {Count} pending payments for pool {PoolId}", 
                response_data?.Payments?.Count ?? 0, response_data?.PoolId ?? "unknown");
            
            return response_data?.Payments ?? new List<PendingPayment>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error while fetching pending payments");
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Request timed out while fetching pending payments");
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize pending payments response");
            throw;
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Testing connection to API: {BaseUrl}", _config.ApiBaseUrl);
            
            var response = await _httpClient.GetAsync("/api/pools", cancellationToken);
            var isConnected = response.IsSuccessStatusCode;
            
            if (isConnected)
            {
                _logger.LogInformation("Successfully connected to API");
            }
            else
            {
                _logger.LogWarning("Failed to connect to API. Status: {StatusCode}", response.StatusCode);
            }
            
            return isConnected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing API connection");
            return false;
        }
    }

    public async Task<bool> MarkPaymentCompletedAsync(string poolId, PendingPayment payment, string transactionId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Marking payment as completed: {Address} = {Amount} ALPHA, TxId: {TxId}", 
                payment.Address, payment.Amount, transactionId);

            var request = new
            {
                PaymentId = payment.Id,
                TransactionId = transactionId
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"/api/admin/pools/{poolId}/payments/complete", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully marked payment as completed: {Address} = {Amount} ALPHA", 
                    payment.Address, payment.Amount);
                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to mark payment as completed. Status: {StatusCode}, Response: {Response}", 
                    response.StatusCode, errorContent);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking payment as completed for {Address}", payment.Address);
            return false;
        }
    }
}