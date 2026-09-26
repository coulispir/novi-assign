using Core.Service.Entities;
using Core.Service.Exceptions;

namespace Core.Service.Strategies;

public class SubtractFundsStrategy : IBalanceStrategy
{
    public string Name => "SubtractFundsStrategy";

    public void Apply(AccountWallet wallet, decimal amount)
    {
        // Check balance bounds manually first to throw your dedicated domain exception shape
        if (wallet.Balance - amount < 0)
        {
            throw new InsufficientFundsException("Wallet lacks sufficient funds to complete this operation.");
        }
        wallet.Debit(amount);
    }
}
