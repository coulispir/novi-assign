using Core.Service.Entities;

namespace Core.Service.Strategies;

public class AddFundsStrategy : IBalanceStrategy
{
    public string Name => "AddFundsStrategy";

    public void Apply(AccountWallet wallet, decimal amount)
    {
        wallet.Credit(amount);
    }
}
