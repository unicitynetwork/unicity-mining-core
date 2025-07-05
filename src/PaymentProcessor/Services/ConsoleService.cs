using PaymentProcessor.Models;
using Spectre.Console;

namespace PaymentProcessor.Services;

public class ConsoleService : IConsoleService
{
    public void DisplayWelcome()
    {
        var rule = new Rule("[bold blue]Payment Processor[/]");
        rule.Centered();
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
    }

    public void DisplayConnectionStatus(bool isConnected, string apiUrl)
    {
        var status = isConnected ? "[green]Connected[/]" : "[red]Disconnected[/]";
        var panel = new Panel($"API Status: {status}\nURL: {apiUrl}")
        {
            Header = new PanelHeader("Connection Status"),
            Border = BoxBorder.Rounded
        };
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public string SelectWallet(List<WalletInfo> wallets)
    {
        if (wallets.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No wallets available[/]");
            return string.Empty;
        }

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn("Wallet Name");
        table.AddColumn("Balance");
        table.AddColumn("Addresses");

        foreach (var wallet in wallets)
        {
            var balanceColor = wallet.Balance > 0 ? "green" : "red";
            table.AddRow(
                wallet.WalletName,
                $"[{balanceColor}]{wallet.Balance:F8} ALPHA[/]",
                wallet.Addresses.Count.ToString()
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var choices = wallets.Select(w => $"{w.WalletName} - {w.Balance:F8} ALPHA").ToList();
        
        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a wallet for payments:")
                .PageSize(10)
                .MoreChoicesText("[grey](Move up and down to see more wallets)[/]")
                .AddChoices(choices)
        );

        var selectedWalletName = selection.Split(' ')[0];
        return selectedWalletName;
    }

    public void DisplayWalletInfo(WalletInfo wallet)
    {
        var balanceColor = wallet.Balance > 0 ? "green" : "red";
        var panel = new Panel($"Wallet: [bold]{wallet.WalletName}[/]\nBalance: [{balanceColor}]{wallet.Balance:F8} ALPHA[/]\nAddresses: {wallet.Addresses.Count}")
        {
            Header = new PanelHeader("Selected Wallet"),
            Border = BoxBorder.Rounded
        };
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public void DisplayPendingPayments(List<PendingPayment> payments)
    {
        if (payments.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No pending payments found.[/]");
            return;
        }

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn("Index");
        table.AddColumn("Payment ID");
        table.AddColumn("Address");
        table.AddColumn("Amount");
        table.AddColumn("Created");

        for (int i = 0; i < payments.Count; i++)
        {
            var payment = payments[i];
            table.AddRow(
                (i + 1).ToString(),
                payment.Id.ToString(),
                payment.Address,
                $"{payment.Amount:F8}",
                payment.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss")
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public List<PendingPayment> SelectPayments(List<PendingPayment> payments)
    {
        if (payments.Count == 0)
        {
            return new List<PendingPayment>();
        }

        var choices = new List<string> { "Select All" };
        choices.AddRange(payments.Select((p, i) => $"{i + 1}. {p.Address} - {p.Amount:F8}"));

        var selection = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Select payments to process:")
                .NotRequired()
                .PageSize(10)
                .MoreChoicesText("[grey](Move up and down to reveal more choices)[/]")
                .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
                .AddChoices(choices)
        );

        if (selection.Contains("Select All"))
        {
            return payments.ToList();
        }

        var selectedPayments = new List<PendingPayment>();
        foreach (var choice in selection)
        {
            var index = int.Parse(choice.Split('.')[0]) - 1;
            selectedPayments.Add(payments[index]);
        }

        return selectedPayments;
    }

    public bool ConfirmProcessing(List<PendingPayment> selectedPayments)
    {
        if (selectedPayments.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No payments selected.[/]");
            return false;
        }

        var totalAmount = selectedPayments.Sum(p => p.Amount);
        
        var panel = new Panel($"Selected: {selectedPayments.Count} payments\nTotal Amount: {totalAmount:F8}")
        {
            Header = new PanelHeader("Processing Summary"),
            Border = BoxBorder.Rounded
        };
        
        AnsiConsole.Write(panel);

        return AnsiConsole.Confirm("Do you want to process these payments?");
    }

    public void DisplayProcessingResults(List<PaymentProcessingResult> results)
    {
        var successful = results.Count(r => r.Success);
        var failed = results.Count(r => !r.Success);

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn("Status");
        table.AddColumn("Address");
        table.AddColumn("Amount");
        table.AddColumn("Transaction ID / Error");

        foreach (var result in results)
        {
            var status = result.Success ? "[green]Success[/]" : "[red]Failed[/]";
            var details = result.Success ? (result.TransactionId ?? "N/A") : (result.Error ?? "Unknown error");
            
            table.AddRow(
                status,
                result.Address,
                $"{result.Amount:F8}",
                details
            );
        }

        AnsiConsole.Write(table);
        
        var summary = new Panel($"[green]Successful: {successful}[/]\n[red]Failed: {failed}[/]")
        {
            Header = new PanelHeader("Processing Results"),
            Border = BoxBorder.Rounded
        };
        
        AnsiConsole.Write(summary);
    }

    public void DisplayError(string message)
    {
        AnsiConsole.MarkupLine($"[red]Error: {message}[/]");
    }

    public void DisplayInfo(string message)
    {
        AnsiConsole.MarkupLine($"[blue]Info: {message}[/]");
    }
}