using System;
using System.Collections.Generic;
using System.Linq;

using Core.Service.Exceptions;

namespace Core.Service.Strategies;

public interface IBalanceStrategyFactory
{
    IBalanceStrategy GetStrategy(BalanceStrategyType type);
}

public class BalanceStrategyFactory : IBalanceStrategyFactory
{
    private readonly Dictionary<BalanceStrategyType, IBalanceStrategy> _strategies = new();

    public BalanceStrategyFactory(IEnumerable<IBalanceStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        foreach (var strategy in strategies)
        {
            if (!_strategies.TryAdd(strategy.Type, strategy))
                throw new InvalidOperationException($"More than one balance strategy is registered for {strategy.Type}.");
        }

        // Every strategy clients can choose must have an implementation, or the host fails at startup, not on a request
        var missing = Enum.GetValues<BalanceStrategyType>().Where(type => !_strategies.ContainsKey(type)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"No balance strategy is registered for: {string.Join(", ", missing)}.");
    }

    public IBalanceStrategy GetStrategy(BalanceStrategyType type)
    {
        // Only reachable with an undefined value cast from code: the API binds strategies by name
        if (!_strategies.TryGetValue(type, out var strategy))
            throw new DomainValidationException($"Supported strategies include: {string.Join(", ", Enum.GetNames<BalanceStrategyType>())}. Received: '{type}'");

        return strategy;
    }
}
