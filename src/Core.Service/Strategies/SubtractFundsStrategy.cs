using Core.Service.Entities;

namespace Core.Service.Strategies;

public class SubtractFundsStrategy : IBalanceStrategy
{
    public BalanceStrategyType Type => BalanceStrategyType.SubtractFundsStrategy;

    public void Apply(AccountWallet wallet, decimal amount)
    {
        // Debit rejects going below zero with InsufficientFundsException
        wallet.Debit(amount);
    }
}
