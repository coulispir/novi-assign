using System;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Handlers;
using Core.Service.Services;
using Core.Service.Strategies;

using FluentAssertions;

using NSubstitute;

namespace Unit.Tests.Application;

public sealed class WalletHandlerTests
{
    private readonly IWalletService _walletService = Substitute.For<IWalletService>();
    private readonly WalletHandler _handler;

    public WalletHandlerTests()
    {
        _handler = new WalletHandler(_walletService);
    }

    [Fact]
    public async Task HandleAdjustmentAsync_WithValidCommand_DelegatesToTheService()
    {
        var expected = new WalletAdjustmentResult(1, "EUR", 15m, IsReplay: false);
        _walletService.AdjustBalanceAsync(1, 5m, "EUR", BalanceStrategyType.AddFundsStrategy, "key-1", Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _handler.HandleAdjustmentAsync(new AdjustBalanceCommand(1, 5m, "EUR", BalanceStrategyType.AddFundsStrategy, "key-1"), CancellationToken.None);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task HandleAdjustmentAsync_WithNonPositiveAmount_ThrowsValidationErrorWithoutCallingTheService(decimal amount)
    {
        var handle = () => _handler.HandleAdjustmentAsync(new AdjustBalanceCommand(1, amount, "EUR", BalanceStrategyType.AddFundsStrategy, "key-1"), CancellationToken.None).AsTask();

        await handle.Should().ThrowAsync<DomainValidationException>().WithMessage("*positive*");
        await AssertServiceNotCalledAsync();
    }

    public static TheoryData<string> InvalidIdempotencyKeys => new()
    {
        string.Empty,
        "   ",
        new string('k', IdempotencyRecord.MaxKeyLength + 1),
    };

    [Theory]
    [MemberData(nameof(InvalidIdempotencyKeys))]
    public async Task HandleAdjustmentAsync_WithMissingOrOversizedIdempotencyKey_ThrowsValidationErrorWithoutCallingTheService(string idempotencyKey)
    {
        var handle = () => _handler.HandleAdjustmentAsync(new AdjustBalanceCommand(1, 5m, "EUR", BalanceStrategyType.AddFundsStrategy, idempotencyKey), CancellationToken.None).AsTask();

        await handle.Should().ThrowAsync<DomainValidationException>().WithMessage("*Idempotency-Key*");
        await AssertServiceNotCalledAsync();
    }

    [Fact]
    public async Task HandleAdjustmentAsync_WithKeyAtTheMaximumLength_IsAccepted()
    {
        var key = new string('k', IdempotencyRecord.MaxKeyLength);

        await _handler.HandleAdjustmentAsync(new AdjustBalanceCommand(1, 5m, "EUR", BalanceStrategyType.AddFundsStrategy, key), CancellationToken.None);

        await _walletService.ReceivedWithAnyArgs(1).AdjustBalanceAsync(default, default, default!, default!, default!, default);
    }

    [Fact]
    public async Task HandleQueryAsync_ReturnsBothTheOriginalAndTheRequestedBalance()
    {
        var wallet = AccountWallet.Create("USD", 100m);
        _walletService.GetConvertedBalanceAsync(1, "GBP", Arg.Any<CancellationToken>()).Returns((wallet, 75.4582m, "GBP"));

        var result = await _handler.HandleQueryAsync(new GetBalanceQuery(1, "GBP"), CancellationToken.None);

        result.Should().Be(new BalanceDisplayResult(wallet.Id, 100m, "USD", 75.4582m, "GBP"));
    }

    private async Task AssertServiceNotCalledAsync()
    {
        await _walletService.DidNotReceiveWithAnyArgs().AdjustBalanceAsync(default, default, default!, default!, default!, default);
    }
}
