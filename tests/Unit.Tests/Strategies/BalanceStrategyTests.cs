using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Strategies;

using FluentAssertions;

namespace Unit.Tests.Strategies;

public sealed class BalanceStrategyTests
{
    private readonly BalanceStrategyFactory _factory = new(
        [new AddFundsStrategy(), new SubtractFundsStrategy(), new ForceSubtractFundsStrategy()]);

    [Theory]
    [InlineData("")]
    [InlineData("TransferFundsStrategy")]
    public void RejectsAnUnknownStrategyAsAValidationError(string strategyName)
    {
        var getStrategy = () => _factory.GetStrategy(strategyName);

        getStrategy.Should().Throw<DomainValidationException>().WithMessage($"*Received: '{strategyName}'*");
    }

    [Fact]
    public void ResolvesStrategiesCaseInsensitively()
    {
        _factory.GetStrategy("subtractfundsstrategy").Should().BeOfType<SubtractFundsStrategy>();
    }

    [Fact]
    public void SubtractFundsRejectsGoingBelowZero()
    {
        var wallet = AccountWallet.Create("EUR", 5m);

        var apply = () => _factory.GetStrategy("SubtractFundsStrategy").Apply(wallet, 6m);

        apply.Should().Throw<InsufficientFundsException>();
        wallet.Balance.Should().Be(5m);
    }

    [Fact]
    public void ForceSubtractFundsAllowsGoingBelowZero()
    {
        var wallet = AccountWallet.Create("EUR", 5m);

        _factory.GetStrategy("ForceSubtractFundsStrategy").Apply(wallet, 6m);

        wallet.Balance.Should().Be(-1m);
    }
}
