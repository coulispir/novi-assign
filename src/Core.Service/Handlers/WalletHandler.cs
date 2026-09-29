using System;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Services;
using Core.Service.Strategies;

namespace Core.Service.Handlers;

public record CreateWalletCommand(string Currency, decimal InitialBalance);
public record AdjustBalanceCommand(long WalletId, decimal Amount, string Currency, BalanceStrategyType Strategy, string? IdempotencyKey);
public record GetBalanceQuery(long WalletId, string? TargetCurrency);
public record BalanceDisplayResult(long WalletId, decimal OriginalBalance, string OriginalCurrency, decimal RequestedBalance, string RequestedCurrency);

public interface IWalletHandler
{
    ValueTask<AccountWallet> HandleCreateAsync(CreateWalletCommand command, CancellationToken cancellationToken);
    ValueTask<WalletAdjustmentResult> HandleAdjustmentAsync(AdjustBalanceCommand command, CancellationToken cancellationToken);
    ValueTask<BalanceDisplayResult> HandleQueryAsync(GetBalanceQuery query, CancellationToken cancellationToken);
}

public class WalletHandler : IWalletHandler
{
    private readonly IWalletService _walletService;

    public WalletHandler(IWalletService walletService)
    {
        _walletService = walletService;
    }

    public async ValueTask<AccountWallet> HandleCreateAsync(CreateWalletCommand command, CancellationToken cancellationToken)
    {
        return await _walletService.CreateAsync(command.Currency, command.InitialBalance, cancellationToken);
    }

    public async ValueTask<WalletAdjustmentResult> HandleAdjustmentAsync(AdjustBalanceCommand command, CancellationToken cancellationToken)
    {
        if (command.Amount <= 0)
            throw new DomainValidationException("The amount parameter must always be a positive number.");

        // The key is optional: without one the adjustment simply runs. A key that is sent must be usable, though, so a
        // blank or oversized one is a client error rather than silently treated as "no key".
        var idempotencyKey = string.IsNullOrEmpty(command.IdempotencyKey) ? null : command.IdempotencyKey;

        if (idempotencyKey is not null && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > IdempotencyRecord.MaxKeyLength))
            throw new DomainValidationException($"The Idempotency-Key header is optional, but when sent it must be 1-{IdempotencyRecord.MaxKeyLength} characters and not blank.");

        return await _walletService.AdjustBalanceAsync(command.WalletId, command.Amount, command.Currency, command.Strategy, idempotencyKey, cancellationToken);
    }

    public async ValueTask<BalanceDisplayResult> HandleQueryAsync(GetBalanceQuery query, CancellationToken cancellationToken)
    {
        var result = await _walletService.GetConvertedBalanceAsync(query.WalletId, query.TargetCurrency, cancellationToken);

        return new BalanceDisplayResult(
            WalletId: result.Wallet.Id,
            OriginalBalance: result.Wallet.Balance,
            OriginalCurrency: result.Wallet.Currency,
            RequestedBalance: result.CalculatedBalance,
            RequestedCurrency: result.TargetCurrency
        );
    }
}
