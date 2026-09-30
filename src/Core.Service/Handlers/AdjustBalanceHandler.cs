using System;
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
using Core.Service.Services;
using Core.Service.Strategies;

using Microsoft.EntityFrameworkCore;

namespace Core.Service.Handlers;

public record AdjustBalanceCommand(long WalletId, decimal Amount, string Currency, BalanceStrategyType Strategy, string? IdempotencyKey);
public record WalletAdjustmentResult(long WalletId, string Currency, decimal Balance, bool IsReplay);

/// <summary>
/// Applies a balance strategy to a wallet, converting the amount to the wallet's currency first. With an
/// idempotency key, a retry of the same request replays the stored result instead of applying it again.
/// </summary>
public interface IAdjustBalanceHandler
{
    ValueTask<WalletAdjustmentResult> HandleAsync(AdjustBalanceCommand command, CancellationToken cancellationToken);
}

public class AdjustBalanceHandler : IAdjustBalanceHandler
{
    private readonly IWalletRepository _walletRepository;
    private readonly SystemDbContext _dbContext;
    private readonly IBalanceStrategyFactory _strategyFactory;
    private readonly ICurrencyRatesProvider _currencyRatesProvider;

    public AdjustBalanceHandler(IWalletRepository walletRepository, SystemDbContext dbContext, IBalanceStrategyFactory strategyFactory, ICurrencyRatesProvider currencyRatesProvider)
    {
        _walletRepository = walletRepository;
        _dbContext = dbContext;
        _strategyFactory = strategyFactory;
        _currencyRatesProvider = currencyRatesProvider;
    }

    public async ValueTask<WalletAdjustmentResult> HandleAsync(AdjustBalanceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Amount <= 0)
            throw new DomainValidationException("The amount parameter must always be a positive number.");

        // The key is optional: without one the adjustment simply runs. A key that is sent must be usable, though, so a
        // blank or oversized one is a client error rather than silently treated as "no key".
        var idempotencyKey = string.IsNullOrEmpty(command.IdempotencyKey) ? null : command.IdempotencyKey;

        if (idempotencyKey is not null && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > IdempotencyRecord.MaxKeyLength))
            throw new DomainValidationException($"The Idempotency-Key header is optional, but when sent it must be 1-{IdempotencyRecord.MaxKeyLength} characters and not blank.");

        if (idempotencyKey is null)
            return await AdjustWithoutIdempotencyAsync(command.WalletId, command.Amount, command.Currency, command.Strategy, cancellationToken);

        return await AdjustWithIdempotencyAsync(command.WalletId, command.Amount, command.Currency, command.Strategy, idempotencyKey, cancellationToken);
    }

    private async ValueTask<WalletAdjustmentResult> AdjustWithIdempotencyAsync(long walletId, decimal amount, string currency, BalanceStrategyType strategyType, string idempotencyKey, CancellationToken cancellationToken)
    {
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
}
