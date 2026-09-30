using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Handlers;
using Core.Service.Interfaces;
using Core.Service.Repositories;

using FluentAssertions;

using NSubstitute;

namespace Unit.Tests.Application;

public sealed class GetBalanceHandlerTests
{
    // Rates against EUR, as published by the ECB
    private static readonly IReadOnlyDictionary<string, decimal> Rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1.1403m,
        ["GBP"] = 0.86045m,
        ["JPY"] = 171.23m,
    };

    private readonly IWalletRepository _walletRepository = Substitute.For<IWalletRepository>();
    private readonly ICurrencyRatesProvider _ratesProvider = Substitute.For<ICurrencyRatesProvider>();
    private readonly GetBalanceHandler _handler;

    public GetBalanceHandlerTests()
    {
        _handler = new GetBalanceHandler(_walletRepository, _ratesProvider);
        _ratesProvider.GetLatestRatesAsync(Arg.Any<CancellationToken>()).Returns(Rates);
    }

    [Fact]
    public async Task HandleAsync_ReturnsBothTheOriginalAndTheRequestedBalance()
    {
        var wallet = ArrangeWallet("USD", 100m);

        var result = await _handler.HandleAsync(new GetBalanceQuery(1, "GBP"), CancellationToken.None);

        result.Should().Be(new BalanceResult(wallet.Id, 100m, "USD", 75.4582m, "GBP"));
    }

    [Theory]
    [InlineData("USD", "GBP", 100, 75.4582)] // 100 / 1.1403 * 0.86045, rounded to 4 decimals
    [InlineData("EUR", "USD", 100, 114.03)]
    [InlineData("USD", "EUR", 114.03, 100)]
    [InlineData("GBP", "JPY", 10, 1990.0052)]
    public async Task HandleAsync_ConvertsThroughEuroAndRoundsToFourDecimals(string walletCurrency, string targetCurrency, decimal balance, decimal expected)
    {
        ArrangeWallet(walletCurrency, balance);

        var result = await _handler.HandleAsync(new GetBalanceQuery(1, targetCurrency), CancellationToken.None);

        result.RequestedBalance.Should().Be(expected);
        result.RequestedCurrency.Should().Be(targetCurrency);
    }

    [Fact]
    public async Task HandleAsync_WithLowerCaseTarget_NormalizesTheCurrency()
    {
        ArrangeWallet("EUR", 100m);

        var result = await _handler.HandleAsync(new GetBalanceQuery(1, "usd"), CancellationToken.None);

        result.RequestedBalance.Should().Be(114.03m);
        result.RequestedCurrency.Should().Be("USD");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("usd")]
    public async Task HandleAsync_WithoutTargetOrWithTheWalletCurrency_ReturnsTheBalanceWithoutLoadingRates(string? targetCurrency)
    {
        ArrangeWallet("USD", 42m);

        var result = await _handler.HandleAsync(new GetBalanceQuery(1, targetCurrency), CancellationToken.None);

        result.RequestedBalance.Should().Be(42m);
        result.RequestedCurrency.Should().Be("USD");
        await _ratesProvider.DidNotReceiveWithAnyArgs().GetLatestRatesAsync(default);
    }

    [Fact]
    public async Task HandleAsync_WithUnknownTargetCurrency_ThrowsUnsupportedCurrency()
    {
        ArrangeWallet("USD", 100m);

        var convert = () => _handler.HandleAsync(new GetBalanceQuery(1, "XYZ"), CancellationToken.None).AsTask();

        await convert.Should().ThrowAsync<UnsupportedCurrencyException>().WithMessage("*USD to XYZ*");
    }

    [Fact]
    public async Task HandleAsync_WithUnknownWallet_ThrowsNotFound()
    {
        _walletRepository.GetByIdReadOnlyAsync(99, Arg.Any<CancellationToken>()).Returns((AccountWallet?)null);

        var convert = () => _handler.HandleAsync(new GetBalanceQuery(99, "USD"), CancellationToken.None).AsTask();

        await convert.Should().ThrowAsync<WalletNotFoundException>();
    }

    private AccountWallet ArrangeWallet(string currency, decimal balance)
    {
        var wallet = AccountWallet.Create(currency, balance);
        _walletRepository.GetByIdReadOnlyAsync(1, Arg.Any<CancellationToken>()).Returns(wallet);
        return wallet;
    }
}
