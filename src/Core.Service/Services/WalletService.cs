using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Data;
using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Interfaces;
using Core.Service.Repositories;
using Core.Service.Strategies;

using Microsoft.EntityFrameworkCore;

namespace Core.Service.Services;

public class WalletService : IWalletService
{
    private readonly IWalletRepository _walletRepository;
    private readonly SystemDbContext _dbContext;
    private readonly IBalanceStrategyFactory _strategyFactory;
    private readonly ICurrencyRatesProvider _currencyRatesProvider;

    public WalletService(IWalletRepository walletRepository, SystemDbContext dbContext, IBalanceStrategyFactory strategyFactory, ICurrencyRatesProvider currencyRatesProvider)
    {
        _walletRepository = walletRepository;
        _dbContext = dbContext;
        _strategyFactory = strategyFactory;
        _currencyRatesProvider = currencyRatesProvider;
    }

    public async ValueTask<AccountWallet> CreateAsync(string currency, decimal initialBalance, CancellationToken cancellationToken)
    {
        var wallet = AccountWallet.Create(currency, initialBalance);
        _walletRepository.Add(wallet);
        await _walletRepository.SaveChangesAsync(cancellationToken);
        return wallet;
    }

    public async ValueTask<WalletAdjustmentResult> AdjustBalanceAsync(long walletId, decimal amount, string currency, BalanceStrategyType strategyType, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (idempotencyKey is null)
            return await AdjustWithoutIdempotencyAsync(walletId, amount, currency, strategyType, cancellationToken);

        var requestHash = ComputeRequestHash(walletId, amount, currency, strategyType);

        var existing = await FindIdempotencyRecordAsync(idempotencyKey, cancellationToken);
        if (existing is not null) return Replay(existing, requestHash);

        var wallet = await ApplyAsync(walletId, amount, currency, strategyType, cancellationToken);

        // Saved in the same SaveChanges as the balance update, so both commit or neither does
        _dbContext.IdempotencyRecords.Add(IdempotencyRecord.Create(idempotencyKey, requestHash, wallet));

        try
        {
            await _walletRepository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Nothing was committed. Drop the stale tracked entities before re-reading.
            _dbContext.ChangeTracker.Clear();

            // A parallel request with the same key committed first (key PK violation, or its wallet update won the RowVersion race)
            var winner = await FindIdempotencyRecordAsync(idempotencyKey, cancellationToken);
            if (winner is not null) return Replay(winner, requestHash);

            if (ex is DbUpdateConcurrencyException)
                throw new ConcurrencyConflictException($"Wallet ID '{walletId}' was modified by another request. Retry with the same Idempotency-Key.");

            throw;
        }

        return new WalletAdjustmentResult(wallet.Id, wallet.Currency, wallet.Balance, IsReplay: false);
    }

    // Without a key there is nothing to replay: apply the adjustment once, as any plain POST would
    private async ValueTask<WalletAdjustmentResult> AdjustWithoutIdempotencyAsync(long walletId, decimal amount, string currency, BalanceStrategyType strategyType, CancellationToken cancellationToken)
    {
        var wallet = await ApplyAsync(walletId, amount, currency, strategyType, cancellationToken);

        try
        {
            await _walletRepository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request changed the wallet first and nothing was committed, so retrying is safe
            throw new ConcurrencyConflictException($"Wallet ID '{walletId}' was modified by another request. Retry the request.");
        }

        return new WalletAdjustmentResult(wallet.Id, wallet.Currency, wallet.Balance, IsReplay: false);
    }

    private async ValueTask<AccountWallet> ApplyAsync(long walletId, decimal amount, string currency, BalanceStrategyType strategyType, CancellationToken cancellationToken)
    {
        var wallet = await _walletRepository.GetByIdAsync(walletId, cancellationToken);
        if (wallet is null) throw new WalletNotFoundException(walletId);

        var strategy = _strategyFactory.GetStrategy(strategyType);
        var walletAmount = await ToWalletCurrencyAsync(amount, currency, wallet, cancellationToken);
        strategy.Apply(wallet, walletAmount);

        return wallet;
    }

    // An adjustment in another currency is converted at the latest rate before the strategy applies it, so balance
    // rules such as "no overdraft" are checked in the wallet's own currency
    private async ValueTask<decimal> ToWalletCurrencyAsync(decimal amount, string currency, AccountWallet wallet, CancellationToken cancellationToken)
    {
        if (string.Equals(wallet.Currency, currency, StringComparison.OrdinalIgnoreCase)) return amount;

        var rates = await _currencyRatesProvider.GetLatestRatesAsync(cancellationToken);
        var walletAmount = CurrencyConverter.Convert(amount, currency, wallet.Currency, rates);

        if (walletAmount <= 0)
            throw new DomainValidationException($"{amount} {currency.ToUpperInvariant()} is less than 0.0001 {wallet.Currency} after conversion; a wallet can't hold a smaller amount.");

        return walletAmount;
    }

    private ValueTask<IdempotencyRecord?> FindIdempotencyRecordAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        return _dbContext.IdempotencyRecords.FindAsync(new object[] { idempotencyKey }, cancellationToken);
    }

    private static WalletAdjustmentResult Replay(IdempotencyRecord record, string requestHash)
    {
        if (record.RequestHash != requestHash)
            throw new IdempotencyKeyReuseException("Idempotency-Key was already used for a different request.");

        return new WalletAdjustmentResult(record.WalletId, record.Currency, record.Balance, IsReplay: true);
    }

    private static string ComputeRequestHash(long walletId, decimal amount, string currency, BalanceStrategyType strategyType)
    {
        // Normalize so that e.g. "10" and "10.00", or "eur" and "EUR", count as the same request
        var normalizedAmount = amount.ToString("0.############################", CultureInfo.InvariantCulture);
        // Hash the strategy's name, never its numeric value: the name is the stable public contract, and it keeps the
        // hash identical to records stored when strategies were plain strings
        var payload = $"{walletId}|{normalizedAmount}|{currency.ToUpperInvariant()}|{strategyType.ToString().ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public async ValueTask<(AccountWallet Wallet, decimal CalculatedBalance, string TargetCurrency)> GetConvertedBalanceAsync(long walletId, string? targetCurrency, CancellationToken cancellationToken)
    {
        var wallet = await _walletRepository.GetByIdAsync(walletId, cancellationToken);
        if (wallet is null) throw new WalletNotFoundException(walletId);

        if (string.IsNullOrWhiteSpace(targetCurrency) || string.Equals(wallet.Currency, targetCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return (wallet, wallet.Balance, wallet.Currency);
        }

        string upperTarget = targetCurrency.ToUpperInvariant();

        var rates = await _currencyRatesProvider.GetLatestRatesAsync(cancellationToken);
        return (wallet, CurrencyConverter.Convert(wallet.Balance, wallet.Currency, upperTarget, rates), upperTarget);
    }
}
