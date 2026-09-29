using Core.Service.Entities;

namespace Core.Service.Strategies;

public class ForceSubtractFundsStrategy : IBalanceStrategy
{
    public BalanceStrategyType Type => BalanceStrategyType.ForceSubtractFundsStrategy;

    public void Apply(AccountWallet wallet, decimal amount)
    {
        wallet.ForceDebit(amount);
    }
}
