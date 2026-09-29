using Core.Service.Entities;

namespace Core.Service.Strategies;

public class AddFundsStrategy : IBalanceStrategy
{
    public BalanceStrategyType Type => BalanceStrategyType.AddFundsStrategy;

    public void Apply(AccountWallet wallet, decimal amount)
    {
        wallet.Credit(amount);
    }
}
