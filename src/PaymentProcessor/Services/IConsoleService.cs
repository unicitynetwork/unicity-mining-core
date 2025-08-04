using PaymentProcessor.Configuration;
using PaymentProcessor.Models;

namespace PaymentProcessor.Services;

public interface IConsoleService
{
    void DisplayWelcome();
    void DisplayConnectionStatus(bool isConnected, string apiUrl);
    string SelectWallet(List<WalletInfo> wallets);
    void DisplayWalletInfo(WalletInfo wallet);
    void DisplayPendingPayments(List<PendingPayment> payments);
    List<PendingPayment> SelectPayments(List<PendingPayment> payments);
    bool ConfirmProcessing(List<PendingPayment> selectedPayments);
    void DisplayProcessingResults(List<PaymentProcessingResult> results);
    void DisplayError(string message);
    void DisplayInfo(string message);
    bool PromptForAutomationMode();
    void DisplayAutomationConfig(AutomationConfig config);
}