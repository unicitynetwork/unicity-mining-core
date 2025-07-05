using Microsoft.AspNetCore.Http;
using Miningcore.Configuration;
using NLog;
using System.Security.Claims;

namespace Miningcore.Api.Middleware;

public class ApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ClusterConfig _config;
    private readonly ILogger _logger = LogManager.GetCurrentClassLogger();

    public ApiKeyAuthenticationMiddleware(RequestDelegate next, ClusterConfig config)
    {
        _next = next;
        _config = config;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only check admin API endpoints
        if (context.Request.Path.StartsWithSegments("/api/admin"))
        {
            if (!IsValidApiKey(context))
            {
                _logger.Warn($"Unauthorized admin API access attempt from {context.Connection.RemoteIpAddress}");
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized: Invalid or missing API key");
                return;
            }

            // Add authentication claim for authorized requests
            var identity = new ClaimsIdentity("ApiKey");
            identity.AddClaim(new Claim(ClaimTypes.Name, "PaymentProcessor"));
            context.User = new ClaimsPrincipal(identity);
        }

        await _next(context);
    }

    private bool IsValidApiKey(HttpContext context)
    {
        // Check if API keys are configured
        if (_config.Api?.AdminApiKeys == null || _config.Api.AdminApiKeys.Length == 0)
        {
            _logger.Warn("No admin API keys configured - denying access");
            return false;
        }

        // Get Authorization header
        if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            return false;
        }

        var authHeaderValue = authHeader.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeaderValue))
        {
            return false;
        }

        // Check for Bearer token format
        if (!authHeaderValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var apiKey = authHeaderValue.Substring("Bearer ".Length).Trim();
        
        // Validate against configured API keys
        return _config.Api.AdminApiKeys.Contains(apiKey);
    }
}