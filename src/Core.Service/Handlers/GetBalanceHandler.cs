using System;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Exceptions;
using Core.Service.Interfaces;
using Core.Service.Repositories;
using Core.Service.Services;

namespace Core.Service.Handlers;

public record GetBalanceQuery(long WalletId, string? TargetCurrency);
public record BalanceResult(long WalletId, decimal OriginalBalance, string OriginalCurrency, decimal RequestedBalance, string RequestedCurrency);

/// <summary>
/// Reads a wallet's balance, converted to <see cref="GetBalanceQuery.TargetCurrency"/> at the latest rates when one is
/// given. Read-only: the wallet is loaded without change tracking.
/// </summary>
public interface IGetBalanceHandler
{
    ValueTask<BalanceResult> HandleAsync(GetBalanceQuery query, CancellationToken cancellationToken);
}

public class GetBalanceHandler : IGetBalanceHandler
{
    private readonly IWalletRepository _walletRepository;
    private readonly ICurrencyRatesProvider _currencyRatesProvider;

    public GetBalanceHandler(IWalletRepository walletRepository, ICurrencyRatesProvider currencyRatesProvider)
    {
        _walletRepository = walletRepository;
        _currencyRatesProvider = currencyRatesProvider;
    }

    public async ValueTask<BalanceResult> HandleAsync(GetBalanceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var wallet = await _walletRepository.GetByIdReadOnlyAsync(query.WalletId, cancellationToken);
        if (wallet is null) throw new WalletNotFoundException(query.WalletId);

        if (string.IsNullOrWhiteSpace(query.TargetCurrency) || string.Equals(wallet.Currency, query.TargetCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return new BalanceResult(wallet.Id, wallet.Balance, wallet.Currency, wallet.Balance, wallet.Currency);
        }

        string upperTarget = query.TargetCurrency.ToUpperInvariant();

        var rates = await _currencyRatesProvider.GetLatestRatesAsync(cancellationToken);
        var converted = CurrencyConverter.Convert(wallet.Balance, wallet.Currency, upperTarget, rates);

        return new BalanceResult(wallet.Id, wallet.Balance, wallet.Currency, converted, upperTarget);
    }
}
