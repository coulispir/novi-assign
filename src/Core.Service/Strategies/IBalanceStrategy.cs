using Core.Service.Entities;

namespace Core.Service.Strategies;

public interface IBalanceStrategy
{
    string Name { get; }
    void Apply(AccountWallet wallet, decimal amount);
}