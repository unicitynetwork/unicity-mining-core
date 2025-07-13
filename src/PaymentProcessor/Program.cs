using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaymentProcessor.Configuration;
using PaymentProcessor.Services;
using Serilog;

namespace PaymentProcessor;

class Program
{
    static async Task Main(string[] args)
    {
        // Run test instead of normal application
        if (args.Length > 0 && args[0] == "test")
        {
            await TestMain.TestPaymentProcessorAsync();
            return;
        }

        // Initialize Serilog early
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        try
        {
            Log.Information("Starting Payment Processor application");
            
            var host = CreateHostBuilder(args).Build();
            
            var app = host.Services.GetRequiredService<PaymentProcessorApp>();
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application failed to start");
            Console.WriteLine($"Application failed to start: {ex.Message}");
            Environment.Exit(1);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddEnvironmentVariables();
                config.AddCommandLine(args);
            })
            .ConfigureServices((context, services) =>
            {
                var config = new PaymentProcessorConfig();
                context.Configuration.GetSection("PaymentProcessor").Bind(config);
                services.AddSingleton(config);

                services.AddHttpClient<IPaymentApiClient, PaymentApiClient>();
                services.AddHttpClient<AlphaRpcClient>();
                services.AddSingleton<IAlphaRpcClient, AlphaRpcClient>();
                services.AddSingleton<IConsoleService, ConsoleService>();
                services.AddSingleton<IPaymentCompletionTracker, FilePaymentCompletionTracker>();
                services.AddSingleton<IAlphaPaymentService, AlphaPaymentService>();
                services.AddSingleton<IPaymentProcessor, Services.PaymentProcessor>();
                services.AddSingleton<PaymentProcessorApp>();
            })
            .UseSerilog();
}