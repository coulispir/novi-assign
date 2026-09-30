using System;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Data;
using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Handlers;
using Core.Service.Interfaces;
using Core.Service.Repositories;
using Core.Service.Strategies;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using NSubstitute;

namespace Unit.Tests.Application;

/// <summary>
/// Input validation and the path without an idempotency key. The idempotent path depends on SQL Server behaviour
/// (row versions, key violations), so the functional tests cover it against a real database instead of a mocked one.
/// </summary>
public sealed class AdjustBalanceHandlerTests : IDisposable
{
    private readonly IWalletRepository _walletRepository = Substitute.For<IWalletRepository>();

    // Only the idempotent path touches the context; it is needed to construct the handler
    private readonly SystemDbContext _unusedDbContext = new(new DbContextOptionsBuilder<SystemDbContext>().Options);
    private readonly AdjustBalanceHandler _handler;

    public AdjustBalanceHandlerTests()
    {
        // The real factory and strategies, so these tests also cover resolving the strategy the client chose
        var strategyFactory = new BalanceStrategyFactory(new IBalanceStrategy[]
        {
            new AddFundsStrategy(),
            new SubtractFundsStrategy(),
            new ForceSubtractFundsStrategy(),
        });

        _handler = new AdjustBalanceHandler(_walletRepository, _unusedDbContext, strategyFactory, Substitute.For<ICurrencyRatesProvider>());
    }

    public void Dispose() => _unusedDbContext.Dispose();

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task HandleAsync_WithNonPositiveAmount_ThrowsValidationErrorWithoutLoadingTheWallet(decimal amount)
    {
        var handle = () => _handler.HandleAsync(Command(amount, idempotencyKey: "key-1"), CancellationToken.None).AsTask();

        await handle.Should().ThrowAsync<DomainValidationException>().WithMessage("*positive*");
        await AssertWalletNotLoadedAsync();
    }

    public static TheoryData<string> InvalidIdempotencyKeys => new()
    {
        "   ",
        new string('k', IdempotencyRecord.MaxKeyLength + 1),
    };

    [Theory]
    [MemberData(nameof(InvalidIdempotencyKeys))]
    public async Task HandleAsync_WithBlankOrOversizedIdempotencyKey_ThrowsValidationErrorWithoutLoadingTheWallet(string idempotencyKey)
    {
        var handle = () => _handler.HandleAsync(Command(5m, idempotencyKey), CancellationToken.None).AsTask();

        await handle.Should().ThrowAsync<DomainValidationException>().WithMessage("*Idempotency-Key*");
        await AssertWalletNotLoadedAsync();
    }

    [Theory]
    [InlineData(BalanceStrategyType.AddFundsStrategy, 15)]
    [InlineData(BalanceStrategyType.SubtractFundsStrategy, 5)]
    [InlineData(BalanceStrategyType.ForceSubtractFundsStrategy, 5)]
    public async Task HandleAsync_WithoutAnIdempotencyKey_AppliesTheChosenStrategyAndSaves(BalanceStrategyType strategy, decimal expectedBalance)
    {
        var wallet = ArrangeWallet("EUR", 10m);

        var result = await _handler.HandleAsync(Command(5m, idempotencyKey: null, strategy), CancellationToken.None);

        result.Should().Be(new WalletAdjustmentResult(wallet.Id, "EUR", expectedBalance, IsReplay: false));
        await _walletRepository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithAnEmptyIdempotencyKey_AdjustsWithoutIdempotency()
    {
        ArrangeWallet("EUR", 10m);

        // Reaching the idempotency records would fail: the context has no database provider
        var result = await _handler.HandleAsync(Command(5m, idempotencyKey: ""), CancellationToken.None);

        result.Balance.Should().Be(15m);
    }

    [Fact]
    public async Task HandleAsync_WhenAnotherRequestChangedTheWalletFirst_ThrowsConcurrencyConflict()
    {
        ArrangeWallet("EUR", 10m);
        _walletRepository.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromException(new DbUpdateConcurrencyException()));

        var handle = () => _handler.HandleAsync(Command(5m, idempotencyKey: null), CancellationToken.None).AsTask();

        await handle.Should().ThrowAsync<ConcurrencyConflictException>();
    }

    [Fact]
    public async Task HandleAsync_WithUnknownWallet_ThrowsNotFound()
    {
        _walletRepository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns((AccountWallet?)null);

        var handle = () => _handler.HandleAsync(Command(5m, idempotencyKey: null), CancellationToken.None).AsTask();

        await handle.Should().ThrowAsync<WalletNotFoundException>();
    }

    private static AdjustBalanceCommand Command(decimal amount, string? idempotencyKey, BalanceStrategyType strategy = BalanceStrategyType.AddFundsStrategy) =>
        new(1, amount, "EUR", strategy, idempotencyKey);

    private AccountWallet ArrangeWallet(string currency, decimal balance)
    {
        var wallet = AccountWallet.Create(currency, balance);
        _walletRepository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(wallet);
        return wallet;
    }

    private async Task AssertWalletNotLoadedAsync()
    {
        await _walletRepository.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
    }
}
