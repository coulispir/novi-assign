using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Data;
using Core.Service.Entities;
using Core.Service.Exceptions;
using Core.Service.Interfaces;
using Core.Service.Repositories;
using Core.Service.Services;
using Core.Service.Strategies;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using NSubstitute;

namespace Unit.Tests.Application;

/// <summary>
/// Balance conversion only. Balance adjustments depend on SQL Server behaviour (row versions, key violations), so the
/// functional tests cover them against a real database instead of a mocked one.
/// </summary>
public sealed class WalletServiceConversionTests : IDisposable
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

    // The conversion path never touches the context; it is only needed to construct the service
    private readonly SystemDbContext _unusedDbContext = new(new DbContextOptionsBuilder<SystemDbContext>().Options);
    private readonly WalletService _service;

    public WalletServiceConversionTests()
    {
        _service = new WalletService(_walletRepository, _unusedDbContext, Substitute.For<IBalanceStrategyFactory>(), _ratesProvider);
        _ratesProvider.GetLatestRatesAsync(Arg.Any<CancellationToken>()).Returns(Rates);
    }

    public void Dispose() => _unusedDbContext.Dispose();

    [Theory]
    [InlineData("USD", "GBP", 100, 75.4582)] // 100 / 1.1403 * 0.86045, rounded to 4 decimals
    [InlineData("EUR", "USD", 100, 114.03)]
    [InlineData("USD", "EUR", 114.03, 100)]
    [InlineData("GBP", "JPY", 10, 1990.0052)]
    public async Task GetConvertedBalanceAsync_ConvertsThroughEuroAndRoundsToFourDecimals(string walletCurrency, string targetCurrency, decimal balance, decimal expected)
    {
        ArrangeWallet(walletCurrency, balance);

        var (_, calculatedBalance, currency) = await _service.GetConvertedBalanceAsync(1, targetCurrency, CancellationToken.None);

        calculatedBalance.Should().Be(expected);
        currency.Should().Be(targetCurrency);
    }

    [Fact]
    public async Task GetConvertedBalanceAsync_WithLowerCaseTarget_NormalizesTheCurrency()
    {
        ArrangeWallet("EUR", 100m);

        var (_, calculatedBalance, currency) = await _service.GetConvertedBalanceAsync(1, "usd", CancellationToken.None);

        calculatedBalance.Should().Be(114.03m);
        currency.Should().Be("USD");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("usd")]
    public async Task GetConvertedBalanceAsync_WithoutTargetOrWithTheWalletCurrency_ReturnsTheBalanceWithoutLoadingRates(string? targetCurrency)
    {
        ArrangeWallet("USD", 42m);

        var (_, calculatedBalance, currency) = await _service.GetConvertedBalanceAsync(1, targetCurrency, CancellationToken.None);

        calculatedBalance.Should().Be(42m);
        currency.Should().Be("USD");
        await _ratesProvider.DidNotReceiveWithAnyArgs().GetLatestRatesAsync(default);
    }

    [Fact]
    public async Task GetConvertedBalanceAsync_WithUnknownTargetCurrency_ThrowsUnsupportedCurrency()
    {
        ArrangeWallet("USD", 100m);

        var convert = () => _service.GetConvertedBalanceAsync(1, "XYZ", CancellationToken.None).AsTask();

        await convert.Should().ThrowAsync<UnsupportedCurrencyException>().WithMessage("*USD to XYZ*");
    }

    [Fact]
    public async Task GetConvertedBalanceAsync_WithUnknownWallet_ThrowsNotFound()
    {
        _walletRepository.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((AccountWallet?)null);

        var convert = () => _service.GetConvertedBalanceAsync(99, "USD", CancellationToken.None).AsTask();

        await convert.Should().ThrowAsync<WalletNotFoundException>();
    }

    private void ArrangeWallet(string currency, decimal balance)
    {
        _walletRepository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(AccountWallet.Create(currency, balance));
    }
}
