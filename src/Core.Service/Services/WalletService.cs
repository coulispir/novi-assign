using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Data;
using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Repositories;
using Core.Service.Strategies;

using Microsoft.EntityFrameworkCore;

namespace Core.Service.Services;

public class WalletService : IWalletService
{
    private readonly IWalletRepository _walletRepository;
    private readonly SystemDbContext _dbContext;
    private readonly IBalanceStrategyFactory _strategyFactory;

    public WalletService(IWalletRepository walletRepository, SystemDbContext dbContext, IBalanceStrategyFactory strategyFactory)
    {
        _walletRepository = walletRepository;
        _dbContext = dbContext;
        _strategyFactory = strategyFactory;
    }

    public async ValueTask<AccountWallet> CreateAsync(string currency, decimal initialBalance, CancellationToken cancellationToken)
    {
        var wallet = AccountWallet.Create(currency, initialBalance);
        _walletRepository.Add(wallet);
        await _walletRepository.SaveChangesAsync(cancellationToken);
        return wallet;
    }

    public async ValueTask<WalletAdjustmentResult> AdjustBalanceAsync(long walletId, decimal amount, string currency, string strategyName, string idempotencyKey, CancellationToken cancellationToken)
    {
        var requestHash = ComputeRequestHash(walletId, amount, currency, strategyName);

        var existing = await FindIdempotencyRecordAsync(idempotencyKey, cancellationToken);
        if (existing is not null) return Replay(existing, requestHash);

        var wallet = await _walletRepository.GetByIdAsync(walletId, cancellationToken);
        if (wallet is null) throw new KeyNotFoundException($"Wallet ID '{walletId}' not found.");

        if (!string.Equals(wallet.Currency, currency, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Currency mismatch. Transaction must match wallet base currency: {wallet.Currency}.");

        var strategy = _strategyFactory.GetStrategy(strategyName);
        strategy.Apply(wallet, amount);

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

    private static string ComputeRequestHash(long walletId, decimal amount, string currency, string strategyName)
    {
        // Normalize so that e.g. "10" and "10.00", or "eur" and "EUR", count as the same request
        var normalizedAmount = amount.ToString("0.############################", CultureInfo.InvariantCulture);
        var payload = $"{walletId}|{normalizedAmount}|{currency.ToUpperInvariant()}|{strategyName.ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public async ValueTask<(AccountWallet Wallet, decimal CalculatedBalance, string TargetCurrency)> GetConvertedBalanceAsync(long walletId, string? targetCurrency, CancellationToken cancellationToken)
    {
        var wallet = await _walletRepository.GetByIdAsync(walletId, cancellationToken);
        if (wallet is null) throw new KeyNotFoundException($"Wallet ID '{walletId}' not found.");

        if (string.IsNullOrWhiteSpace(targetCurrency) || string.Equals(wallet.Currency, targetCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return (wallet, wallet.Balance, wallet.Currency);
        }

        string upperTarget = targetCurrency.ToUpperInvariant();

        var rates = await _dbContext.CurrencyValues
            .Where(r => r.CurrencyCode == wallet.Currency || r.CurrencyCode == upperTarget)
            .OrderByDescending(r => r.RateDate)
            .ToListAsync(cancellationToken);

        var walletCurrencyRate = wallet.Currency == "EUR" ? 1.0m : rates.FirstOrDefault(r => r.CurrencyCode == wallet.Currency)?.Rate;
        var targetCurrencyRate = upperTarget == "EUR" ? 1.0m : rates.FirstOrDefault(r => r.CurrencyCode == upperTarget)?.Rate;

        if (walletCurrencyRate is null || targetCurrencyRate is null || walletCurrencyRate == 0)
            throw new InvalidOperationException($"Exchange metrics unavailable for converting {wallet.Currency} to {upperTarget}.");

        decimal outputBalance = (wallet.Balance / walletCurrencyRate.Value) * targetCurrencyRate.Value;
        return (wallet, Math.Round(outputBalance, 4), upperTarget);
    }
}
