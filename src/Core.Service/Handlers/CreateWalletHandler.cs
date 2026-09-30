using System;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;
using Core.Service.Repositories;

namespace Core.Service.Handlers;

public record CreateWalletCommand(string Currency, decimal InitialBalance);
public record WalletResult(long WalletId, string Currency, decimal Balance);

/// <summary>
/// Creates a wallet. The currency and initial balance are validated by <see cref="AccountWallet.Create"/>.
/// </summary>
public interface ICreateWalletHandler
{
    ValueTask<WalletResult> HandleAsync(CreateWalletCommand command, CancellationToken cancellationToken);
}

public class CreateWalletHandler : ICreateWalletHandler
{
    private readonly IWalletRepository _walletRepository;

    public CreateWalletHandler(IWalletRepository walletRepository)
    {
        _walletRepository = walletRepository;
    }

    public async ValueTask<WalletResult> HandleAsync(CreateWalletCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var wallet = AccountWallet.Create(command.Currency, command.InitialBalance);
        _walletRepository.Add(wallet);
        await _walletRepository.SaveChangesAsync(cancellationToken);

        return new WalletResult(wallet.Id, wallet.Currency, wallet.Balance);
    }
}
