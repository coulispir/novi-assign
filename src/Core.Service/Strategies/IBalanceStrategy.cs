using Core.Service.Entities;

namespace Core.Service.Strategies;

public interface IBalanceStrategy
{
    BalanceStrategyType Type { get; }
    void Apply(AccountWallet wallet, decimal amount);
}
