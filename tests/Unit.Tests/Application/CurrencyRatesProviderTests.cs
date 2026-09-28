using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Interfaces;
using Core.Service.Services;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

namespace Unit.Tests.Application;

public sealed class CurrencyRatesProviderTests
{
    private static readonly IReadOnlyDictionary<string, decimal> CachedRates = new Dictionary<string, decimal> { ["USD"] = 1.12m };
    private static readonly IReadOnlyDictionary<string, decimal> DatabaseRates = new Dictionary<string, decimal> { ["USD"] = 1.10m };
    private static readonly IReadOnlyDictionary<string, decimal> NoRates = new Dictionary<string, decimal>();

    private readonly ICurrencyRatesCache _cache = Substitute.For<ICurrencyRatesCache>();
    private readonly ICurrencyValueRepository _repository = Substitute.For<ICurrencyValueRepository>();
    private readonly CurrencyRatesProvider _provider;

    public CurrencyRatesProviderTests()
    {
        _provider = new CurrencyRatesProvider(_cache, _repository, NullLogger<CurrencyRatesProvider>.Instance);
    }

    [Fact]
    public async Task GetLatestRatesAsync_OnCacheHit_ServesCachedRatesWithoutQueryingTheDatabase()
    {
        _cache.GetAsync().Returns(CachedRates);

        var rates = await _provider.GetLatestRatesAsync();

        rates.Should().BeSameAs(CachedRates);
        await _repository.DidNotReceiveWithAnyArgs().GetLatestRatesAsync(default);
    }

    [Fact]
    public async Task GetLatestRatesAsync_OnCacheMiss_ReadsTheDatabaseAndFillsTheCacheOnlyIfStillEmpty()
    {
        _cache.GetAsync().Returns((IReadOnlyDictionary<string, decimal>?)null);
        _repository.GetLatestRatesAsync(Arg.Any<CancellationToken>()).Returns(DatabaseRates);

        var rates = await _provider.GetLatestRatesAsync();

        rates.Should().BeSameAs(DatabaseRates);
        await _cache.Received(1).AddIfMissingAsync(DatabaseRates);
        await _cache.DidNotReceiveWithAnyArgs().ReplaceAsync(default!);
    }

    [Fact]
    public async Task GetLatestRatesAsync_WithEmptyDatabase_DoesNotCacheAnything()
    {
        _cache.GetAsync().Returns((IReadOnlyDictionary<string, decimal>?)null);
        _repository.GetLatestRatesAsync(Arg.Any<CancellationToken>()).Returns(NoRates);

        var rates = await _provider.GetLatestRatesAsync();

        rates.Should().BeEmpty();
        await _cache.DidNotReceiveWithAnyArgs().AddIfMissingAsync(default!);
    }

    [Fact]
    public async Task RefreshCacheAsync_WithStoredRates_ReplacesTheCachedSnapshot()
    {
        _repository.GetLatestRatesAsync(Arg.Any<CancellationToken>()).Returns(DatabaseRates);
        _cache.ReplaceAsync(DatabaseRates).Returns(true);

        var refreshed = await _provider.RefreshCacheAsync();

        refreshed.Should().BeTrue();
        await _cache.Received(1).ReplaceAsync(DatabaseRates);
    }

    [Fact]
    public async Task RefreshCacheAsync_WhenCacheIsUnavailable_ReturnsFalse()
    {
        _repository.GetLatestRatesAsync(Arg.Any<CancellationToken>()).Returns(DatabaseRates);
        _cache.ReplaceAsync(DatabaseRates).Returns(false);

        (await _provider.RefreshCacheAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RefreshCacheAsync_WithNoStoredRates_SkipsTheCache()
    {
        _repository.GetLatestRatesAsync(Arg.Any<CancellationToken>()).Returns(NoRates);

        var refreshed = await _provider.RefreshCacheAsync();

        refreshed.Should().BeFalse();
        await _cache.DidNotReceiveWithAnyArgs().ReplaceAsync(default!);
    }
}
