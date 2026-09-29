using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;

using App.Host.Infrastructure.Caching;

using Core.Service.Interfaces;

using FluentAssertions;

using Functional.Tests.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

using StackExchange.Redis;

namespace Functional.Tests;

/// <summary>
/// The full rates path: ECB feed -> sync job -> SQL Server -> Redis snapshot -> conversion endpoint.
/// </summary>
[Collection(WalletApiCollection.Name)]
public sealed class CurrencyConversionTests
{
    private readonly WalletApiFactory _factory;
    private readonly WalletApiClient _api;

    public CurrencyConversionTests(WalletApiFactory factory)
    {
        _factory = factory;
        _api = new WalletApiClient(factory.CreateClient());
    }

    private IDatabase Redis => _factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    [Fact]
    public async Task ConvertsTheBalanceUsingTheSyncedEcbRates()
    {
        var wallet = await _api.CreateWalletAsync(FakeEcbFeed.StableCurrencyA, 100m);

        var balance = await _api.GetBalanceAsync(wallet.Id, FakeEcbFeed.StableCurrencyB.ToLowerInvariant());

        balance.Should().Be(new BalanceDto(wallet.Id, 100m, FakeEcbFeed.StableCurrencyA, ExpectedConversion(100m, FakeEcbFeed.StableCurrencyA, FakeEcbFeed.StableCurrencyB), FakeEcbFeed.StableCurrencyB));
    }

    [Fact]
    public async Task RejectsAConversionToAnUnknownCurrency()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 100m);

        using var response = await _api.GetAsync(wallet.Id, "XYZ");

        await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest, "unsupported_currency");
    }

    [Fact]
    public async Task ServesRatesFromTheCacheRatherThanTheDatabase()
    {
        const string currency = FakeEcbFeed.CurrencyOverriddenInCache;
        var wallet = await _api.CreateWalletAsync("EUR", 100m);
        var cache = _factory.Services.GetRequiredService<ICurrencyRatesCache>();

        // A rate that exists only in Redis: if the endpoint returns it, it did not read SQL Server
        await cache.ReplaceAsync(new Dictionary<string, decimal>(FakeEcbFeed.InitialRates) { [currency] = 2m });

        try
        {
            (await _api.GetBalanceAsync(wallet.Id, currency)).RequestedBalance.Should().Be(200m);
        }
        finally
        {
            await RefreshCacheFromDatabaseAsync();
        }

        (await _api.GetBalanceAsync(wallet.Id, currency)).RequestedBalance.Should().Be(ExpectedConversion(100m, "EUR", currency));
    }

    [Fact]
    public async Task FallsBackToTheDatabaseAndRefillsAnEmptyCache()
    {
        var wallet = await _api.CreateWalletAsync(FakeEcbFeed.StableCurrencyA, 100m);
        await Redis.KeyDeleteAsync(RedisCurrencyRatesCache.Key);

        var balance = await _api.GetBalanceAsync(wallet.Id, FakeEcbFeed.StableCurrencyB);

        balance.RequestedBalance.Should().Be(ExpectedConversion(100m, FakeEcbFeed.StableCurrencyA, FakeEcbFeed.StableCurrencyB));
        (await Redis.HashGetAsync(RedisCurrencyRatesCache.Key, FakeEcbFeed.StableCurrencyB)).Should().Be("0.86045");
        (await Redis.KeyTimeToLiveAsync(RedisCurrencyRatesCache.Key)).Should().BePositive();
    }

    [Fact]
    public async Task ASyncWithNewRatesIsServedOnTheNextRequest()
    {
        const string currency = FakeEcbFeed.CurrencyChangedBySync;
        var wallet = await _api.CreateWalletAsync("EUR", 100m);

        _factory.EcbFeed.PublishNextDay(new Dictionary<string, decimal>(FakeEcbFeed.InitialRates) { [currency] = 0.95m });
        await _factory.RunEcbSyncJobAsync();

        (await _api.GetBalanceAsync(wallet.Id, currency)).RequestedBalance.Should().Be(95m);

        // Leave the feed as it was for any test that runs later
        _factory.EcbFeed.PublishNextDay(FakeEcbFeed.InitialRates);
        await _factory.RunEcbSyncJobAsync();
    }

    [Fact]
    public async Task KeepsTheLastKnownRateOfACurrencyTheEcbStopsPublishing()
    {
        const string currency = FakeEcbFeed.CurrencyDroppedFromFeed;
        var wallet = await _api.CreateWalletAsync("EUR", 100m);

        _factory.EcbFeed.PublishNextDay(FakeEcbFeed.InitialRates.Where(r => r.Key != currency).ToDictionary());
        await _factory.RunEcbSyncJobAsync();

        try
        {
            (await _api.GetBalanceAsync(wallet.Id, currency)).RequestedBalance.Should().Be(ExpectedConversion(100m, "EUR", currency));
        }
        finally
        {
            _factory.EcbFeed.PublishNextDay(FakeEcbFeed.InitialRates);
            await _factory.RunEcbSyncJobAsync();
        }
    }

    private async Task RefreshCacheFromDatabaseAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICurrencyRatesProvider>().RefreshCacheAsync();
    }

    [Theory]
    [InlineData("AddFundsStrategy", 1)]
    [InlineData("SubtractFundsStrategy", -1)]
    public async Task AdjustsInAnotherCurrencyAtTheSyncedRate(string strategy, int sign)
    {
        var wallet = await _api.CreateWalletAsync("EUR", 100m);

        using var response = await _api.AdjustAsync(wallet.Id, 50m, FakeEcbFeed.StableCurrencyA, strategy);

        var expectedBalance = 100m + (sign * ExpectedConversion(50m, FakeEcbFeed.StableCurrencyA, "EUR"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<WalletDto>()).Should().Be(new WalletDto(wallet.Id, "EUR", expectedBalance));
        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(expectedBalance);
    }

    [Fact]
    public async Task ConvertsBetweenTwoNonEuroCurrencies()
    {
        var wallet = await _api.CreateWalletAsync(FakeEcbFeed.StableCurrencyB, 10m);

        using var response = await _api.AdjustAsync(wallet.Id, 20m, FakeEcbFeed.StableCurrencyA, "AddFundsStrategy");

        (await response.Content.ReadFromJsonAsync<WalletDto>())!.Balance
            .Should().Be(10m + ExpectedConversion(20m, FakeEcbFeed.StableCurrencyA, FakeEcbFeed.StableCurrencyB));
    }

    [Fact]
    public async Task ChecksTheOverdraftRuleOnTheConvertedAmount()
    {
        // 100 USD is about 87.70 EUR: within a 90 EUR balance, while 110 USD (about 96.47 EUR) is not
        var wallet = await _api.CreateWalletAsync("EUR", 90m);

        using var rejected = await _api.AdjustAsync(wallet.Id, 110m, FakeEcbFeed.StableCurrencyA, "SubtractFundsStrategy");
        await rejected.ShouldBeErrorAsync(HttpStatusCode.UnprocessableEntity, "insufficient_funds");

        using var allowed = await _api.AdjustAsync(wallet.Id, 100m, FakeEcbFeed.StableCurrencyA, "SubtractFundsStrategy");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(90m - ExpectedConversion(100m, FakeEcbFeed.StableCurrencyA, "EUR"));
    }

    [Fact]
    public async Task RejectsAnAdjustmentInAnUnknownCurrencyWithoutChangingTheBalance()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 100m);

        using var response = await _api.AdjustAsync(wallet.Id, 10m, "XYZ", "AddFundsStrategy");

        await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest, "unsupported_currency");
        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(100m);
    }

    [Fact]
    public async Task RejectsAnAmountThatConvertsToLessThanTheSmallestUnit()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 100m);

        using var response = await _api.AdjustAsync(wallet.Id, 0.00001m, FakeEcbFeed.StableCurrencyA, "AddFundsStrategy");

        await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(100m);
    }

    private static decimal ExpectedConversion(decimal amount, string from, string to)
    {
        static decimal EuroRate(string currency) => currency == "EUR" ? 1m : FakeEcbFeed.InitialRates[currency];

        return Math.Round(amount / EuroRate(from) * EuroRate(to), 4);
    }
}
