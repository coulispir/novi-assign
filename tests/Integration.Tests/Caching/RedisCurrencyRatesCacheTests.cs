using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using App.Host.Infrastructure;
using App.Host.Infrastructure.Caching;

using Core.Service.Interfaces;

using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace Integration.Tests.Caching;

/// <summary>
/// Runs the production cache registrations against a real Redis, so the stored format, the MULTI/EXEC transactions
/// and the fail-open behaviour are exercised exactly as they run in the app.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisCurrencyRatesCacheTests : IAsyncLifetime
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    private readonly RedisFixture _redis;
    private ServiceProvider _services = null!;
    private ICurrencyRatesCache _cache = null!;
    private IDatabase _database = null!;

    public RedisCurrencyRatesCacheTests(RedisFixture redis)
    {
        _redis = redis;
    }

    public async Task InitializeAsync()
    {
        _services = CreateServices(_redis.ConnectionString, TimeToLive.ToString());
        _cache = _services.GetRequiredService<ICurrencyRatesCache>();
        _database = _services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

        // Tests in the collection share one Redis, so start every test from an empty cache
        await _database.KeyDeleteAsync(RedisCurrencyRatesCache.Key);
    }

    public Task DisposeAsync() => _services.DisposeAsync().AsTask();

    [Fact]
    public async Task ReportsAMissWhenNothingIsCached()
    {
        (await _cache.GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task RoundTripsRatesWithoutLosingPrecision()
    {
        var rates = Rates(("USD", 1.1472m), ("JPY", 171.23m), ("IDR", 18987.654321m), ("EUR", 1.0m));

        (await _cache.ReplaceAsync(rates)).Should().BeTrue();

        (await _cache.GetAsync()).Should().BeEquivalentTo(rates);
    }

    [Fact]
    public async Task LooksUpCurrencyCodesCaseInsensitively()
    {
        await _cache.ReplaceAsync(Rates(("USD", 1.1472m)));

        var cached = await _cache.GetAsync();

        cached!["usd"].Should().Be(1.1472m);
    }

    [Fact]
    public async Task ReplaceDropsCurrenciesMissingFromTheNewSnapshot()
    {
        await _cache.ReplaceAsync(Rates(("USD", 1.10m), ("GBP", 0.85m)));

        await _cache.ReplaceAsync(Rates(("USD", 1.12m)));

        (await _cache.GetAsync()).Should().BeEquivalentTo(Rates(("USD", 1.12m)));
    }

    [Fact]
    public async Task AddIfMissingFillsAnEmptyCache()
    {
        (await _cache.AddIfMissingAsync(Rates(("USD", 1.10m)))).Should().BeTrue();

        (await _cache.GetAsync()).Should().BeEquivalentTo(Rates(("USD", 1.10m)));
    }

    [Fact]
    public async Task AddIfMissingNeverOverwritesAnExistingSnapshot()
    {
        // The sync job wrote fresh rates while a request was still reading older ones from the database
        await _cache.ReplaceAsync(Rates(("USD", 1.12m)));

        (await _cache.AddIfMissingAsync(Rates(("USD", 1.10m)))).Should().BeFalse();

        (await _cache.GetAsync()).Should().BeEquivalentTo(Rates(("USD", 1.12m)));
    }

    [Fact]
    public async Task EveryWriteSetsTheConfiguredTimeToLive()
    {
        await _cache.AddIfMissingAsync(Rates(("USD", 1.10m)));
        (await _database.KeyTimeToLiveAsync(RedisCurrencyRatesCache.Key)).Should().BePositive().And.BeLessThanOrEqualTo(TimeToLive);

        await _cache.ReplaceAsync(Rates(("USD", 1.12m)));
        (await _database.KeyTimeToLiveAsync(RedisCurrencyRatesCache.Key)).Should().BePositive().And.BeLessThanOrEqualTo(TimeToLive);
    }

    [Fact]
    public async Task TreatsAnUnreadableSnapshotAsAMiss()
    {
        await _database.HashSetAsync(RedisCurrencyRatesCache.Key, [new HashEntry("USD", "1.10"), new HashEntry("GBP", "not-a-rate")]);

        (await _cache.GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task RejectsAnEmptySnapshot()
    {
        var replaceWithNothing = () => _cache.ReplaceAsync(new Dictionary<string, decimal>());

        await replaceWithNothing.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReportsAMissAndSkipsWritesWhenRedisIsUnavailable()
    {
        // Nothing listens on port 1, so every Redis call fails immediately
        await using var services = CreateServices("127.0.0.1:1,connectTimeout=500", TimeToLive.ToString());
        var cache = services.GetRequiredService<ICurrencyRatesCache>();

        (await cache.GetAsync()).Should().BeNull();
        (await cache.ReplaceAsync(Rates(("USD", 1.10m)))).Should().BeFalse();
        (await cache.AddIfMissingAsync(Rates(("USD", 1.10m)))).Should().BeFalse();
    }

    [Fact]
    public async Task FailsToResolveWithoutAValidTimeToLive()
    {
        await using var services = CreateServices(_redis.ConnectionString, timeToLive: null);

        var resolve = () => services.GetRequiredService<ICurrencyRatesCache>();

        resolve.Should().Throw<OptionsValidationException>().WithMessage($"*{CurrencyRatesCacheOptions.SectionName}*");
    }

    private static ServiceProvider CreateServices(string redisConnectionString, string? timeToLive)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = redisConnectionString,
                [$"{CurrencyRatesCacheOptions.SectionName}:TimeToLive"] = timeToLive,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedis(configuration);
        services.AddCurrencyRatesCache(configuration);

        return services.BuildServiceProvider();
    }

    private static Dictionary<string, decimal> Rates(params (string Currency, decimal Rate)[] rates)
    {
        var result = new Dictionary<string, decimal>();

        foreach (var (currency, rate) in rates)
        {
            result[currency] = rate;
        }

        return result;
    }
}
