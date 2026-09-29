using System;
using System.Linq;

using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Strategies;

using FluentAssertions;

namespace Unit.Tests.Domain;

public sealed class BalanceStrategyTests
{
    private readonly BalanceStrategyFactory _factory = new(
    [
        new AddFundsStrategy(),
        new SubtractFundsStrategy(),
        new ForceSubtractFundsStrategy(),
    ]);

    [Theory]
    [InlineData(BalanceStrategyType.AddFundsStrategy, 15)]
    [InlineData(BalanceStrategyType.SubtractFundsStrategy, 5)]
    [InlineData(BalanceStrategyType.ForceSubtractFundsStrategy, 5)]
    public void EachStrategy_AppliesItsBalanceRule(BalanceStrategyType type, decimal expectedBalance)
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        _factory.GetStrategy(type).Apply(wallet, 5m);

        wallet.Balance.Should().Be(expectedBalance);
    }

    [Fact]
    public void SubtractFunds_BeyondTheBalance_ThrowsInsufficientFunds()
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        var subtract = () => _factory.GetStrategy(BalanceStrategyType.SubtractFundsStrategy).Apply(wallet, 10.01m);

        subtract.Should().Throw<InsufficientFundsException>();
        wallet.Balance.Should().Be(10m);
    }

    [Fact]
    public void ForceSubtractFunds_BeyondTheBalance_AllowsANegativeBalance()
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        _factory.GetStrategy(BalanceStrategyType.ForceSubtractFundsStrategy).Apply(wallet, 25m);

        wallet.Balance.Should().Be(-15m);
    }

    [Fact]
    public void Factory_ResolvesEachTypeToTheStrategyOfThatType()
    {
        foreach (var type in Enum.GetValues<BalanceStrategyType>())
        {
            _factory.GetStrategy(type).Type.Should().Be(type);
        }
    }

    [Fact]
    public void Factory_WithAnUndefinedType_ThrowsValidationErrorListingTheSupportedOnes()
    {
        var resolve = () => _factory.GetStrategy((BalanceStrategyType)42);

        resolve.Should().Throw<DomainValidationException>().WithMessage("*AddFundsStrategy*SubtractFundsStrategy*ForceSubtractFundsStrategy*");
    }

    [Fact]
    public void Factory_WithAStrategyMissing_FailsOnConstruction()
    {
        var create = () => new BalanceStrategyFactory([new AddFundsStrategy(), new SubtractFundsStrategy()]);

        create.Should().Throw<InvalidOperationException>().WithMessage("*ForceSubtractFundsStrategy*");
    }

    [Fact]
    public void Factory_WithTwoStrategiesOfTheSameType_FailsOnConstruction()
    {
        var create = () => new BalanceStrategyFactory(
            [new AddFundsStrategy(), new AddFundsStrategy(), new SubtractFundsStrategy(), new ForceSubtractFundsStrategy()]);

        create.Should().Throw<InvalidOperationException>().WithMessage("*AddFundsStrategy*");
    }

    [Fact]
    public void EveryTypeHasExactlyOneStrategyClass()
    {
        // Guards the host's registrations: adding an enum member without a strategy class fails here first
        var strategyTypes = typeof(IBalanceStrategy).Assembly.GetTypes()
            .Where(t => typeof(IBalanceStrategy).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .Select(t => ((IBalanceStrategy)Activator.CreateInstance(t)!).Type);

        strategyTypes.Should().BeEquivalentTo(Enum.GetValues<BalanceStrategyType>());
    }
}
