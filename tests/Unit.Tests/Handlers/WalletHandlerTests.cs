using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Handlers;
using Core.Service.Services;

using FluentAssertions;

using NSubstitute;

namespace Unit.Tests.Handlers;

public sealed class WalletHandlerTests
{
    private readonly IWalletService _walletService = Substitute.For<IWalletService>();
    private readonly WalletHandler _handler;

    public WalletHandlerTests()
    {
        _handler = new WalletHandler(_walletService);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task RejectsANonPositiveAmountBeforeCallingTheService(decimal amount)
    {
        var adjust = async () => await _handler.HandleAdjustmentAsync(Command(amount: amount), CancellationToken.None);

        await adjust.Should().ThrowAsync<DomainValidationException>();
        await _walletService.DidNotReceiveWithAnyArgs().AdjustBalanceAsync(default, default, default!, default!, default!, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsAMissingIdempotencyKey(string idempotencyKey)
    {
        var adjust = async () => await _handler.HandleAdjustmentAsync(Command(idempotencyKey: idempotencyKey), CancellationToken.None);

        await adjust.Should().ThrowAsync<DomainValidationException>();
    }

    [Fact]
    public async Task RejectsAnIdempotencyKeyLongerThanTheMaximum()
    {
        var tooLong = new string('k', IdempotencyRecord.MaxKeyLength + 1);

        var adjust = async () => await _handler.HandleAdjustmentAsync(Command(idempotencyKey: tooLong), CancellationToken.None);

        await adjust.Should().ThrowAsync<DomainValidationException>();
    }

    private static AdjustBalanceCommand Command(decimal amount = 10m, string idempotencyKey = "key-1") =>
        new(WalletId: 1, amount, Currency: "EUR", Strategy: "AddFundsStrategy", idempotencyKey);
}
