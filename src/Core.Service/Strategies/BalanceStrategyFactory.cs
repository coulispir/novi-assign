using System;
using System.Collections.Generic;
using System.Linq;

namespace Core.Service.Strategies;

public interface IBalanceStrategyFactory
{
    IBalanceStrategy GetStrategy(string strategyName);
}

public class BalanceStrategyFactory : IBalanceStrategyFactory
{
    private readonly Dictionary<string, IBalanceStrategy> _strategies;

    public BalanceStrategyFactory(IEnumerable<IBalanceStrategy> strategies)
    {
        _strategies = strategies.ToDictionary(
            s => s.Name.ToLowerInvariant(),
            s => s
        );
    }

    public IBalanceStrategy GetStrategy(string strategyName)
    {
        if (string.IsNullOrWhiteSpace(strategyName) || !_strategies.TryGetValue(strategyName.ToLowerInvariant(), out var strategy))
        {
            throw new ArgumentException($"Supported strategies include: AddFundsStrategy, SubtractFundsStrategy, ForceSubtractFundsStrategy. Received: '{strategyName}'");
        }
        return strategy;
    }
}
