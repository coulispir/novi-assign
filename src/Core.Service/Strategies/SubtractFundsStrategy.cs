using Core.Service.Entities;

namespace Core.Service.Strategies;

public class SubtractFundsStrategy : IBalanceStrategy
{
    public string Name => "SubtractFundsStrategy";

    public void Apply(AccountWallet wallet, decimal amount)
    {
        // Debit rejects going below zero with InsufficientFundsException
        wallet.Debit(amount);
    }
}
