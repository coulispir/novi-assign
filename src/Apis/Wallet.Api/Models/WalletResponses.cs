using Core.Service.Entities;

namespace Wallet.Api.Models;

public record WalletResponse(
    long Id,
    string Currency,
    decimal Balance)
{
    public static WalletResponse From(AccountWallet wallet) =>
        new(wallet.Id, wallet.Currency, wallet.Balance);
}
