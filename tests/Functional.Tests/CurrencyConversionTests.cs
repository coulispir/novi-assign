using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

    private static decimal ExpectedConversion(decimal amount, string from, string to)
    {
        static decimal EuroRate(string currency) => currency == "EUR" ? 1m : FakeEcbFeed.InitialRates[currency];

        return Math.Round(amount / EuroRate(from) * EuroRate(to), 4);
    }
}
