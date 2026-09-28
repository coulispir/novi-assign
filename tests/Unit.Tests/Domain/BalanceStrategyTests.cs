using System;

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
    [InlineData("AddFundsStrategy", 15)]
    [InlineData("SubtractFundsStrategy", 5)]
    [InlineData("ForceSubtractFundsStrategy", 5)]
    public void EachStrategy_AppliesItsBalanceRule(string strategyName, decimal expectedBalance)
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        _factory.GetStrategy(strategyName).Apply(wallet, 5m);

        wallet.Balance.Should().Be(expectedBalance);
    }

    [Fact]
    public void SubtractFunds_BeyondTheBalance_ThrowsInsufficientFunds()
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        var subtract = () => _factory.GetStrategy("SubtractFundsStrategy").Apply(wallet, 10.01m);

        subtract.Should().Throw<InsufficientFundsException>();
        wallet.Balance.Should().Be(10m);
    }

    [Fact]
    public void ForceSubtractFunds_BeyondTheBalance_AllowsANegativeBalance()
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        _factory.GetStrategy("ForceSubtractFundsStrategy").Apply(wallet, 25m);

        wallet.Balance.Should().Be(-15m);
    }

    [Theory]
    [InlineData("addfundsstrategy")]
    [InlineData("ADDFUNDSSTRATEGY")]
    public void Factory_ResolvesStrategiesCaseInsensitively(string strategyName)
    {
        _factory.GetStrategy(strategyName).Should().BeOfType<AddFundsStrategy>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("TransferFundsStrategy")]
    public void Factory_WithUnknownStrategy_ThrowsListingTheSupportedOnes(string strategyName)
    {
        var resolve = () => _factory.GetStrategy(strategyName);

        resolve.Should().Throw<ArgumentException>().WithMessage("*AddFundsStrategy*SubtractFundsStrategy*ForceSubtractFundsStrategy*");
    }
}
