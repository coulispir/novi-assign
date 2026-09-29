using System;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;
using Core.Service.Strategies;

namespace Core.Service.Services;

public record WalletAdjustmentResult(long WalletId, string Currency, decimal Balance, bool IsReplay);

public interface IWalletService
{
    ValueTask<AccountWallet> CreateAsync(string currency, decimal initialBalance, CancellationToken cancellationToken);
    ValueTask<WalletAdjustmentResult> AdjustBalanceAsync(long walletId, decimal amount, string currency, BalanceStrategyType strategy, string? idempotencyKey, CancellationToken cancellationToken);
    ValueTask<(AccountWallet Wallet, decimal CalculatedBalance, string TargetCurrency)> GetConvertedBalanceAsync(long walletId, string? targetCurrency, CancellationToken cancellationToken);
}
