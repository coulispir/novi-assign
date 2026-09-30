namespace Wallet.Api.Models;

public record WalletResponse(
    long Id,
    string Currency,
    decimal Balance);

public record BalanceResponse(
    long WalletId,
    decimal OriginalBalance,
    string OriginalCurrency,
    decimal RequestedBalance,
    string RequestedCurrency);
