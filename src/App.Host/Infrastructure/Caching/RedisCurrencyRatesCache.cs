using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Core.Service.Interfaces;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace App.Host.Infrastructure.Caching;

/// <summary>
/// Stores the latest rates snapshot as a single Redis hash (currency code -> rate), shared by every node behind the
/// load balancer. Writes run in MULTI/EXEC transactions, so readers never see a half-written snapshot.
/// </summary>
public sealed class RedisCurrencyRatesCache : ICurrencyRatesCache
{
    // Versioned so a future change to the stored format can roll out without old and new nodes misreading each other
    public const string Key = "currency-rates:latest:v1";

    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _timeToLive;
    private readonly ILogger<RedisCurrencyRatesCache> _logger;

    public RedisCurrencyRatesCache(IConnectionMultiplexer redis, IOptions<CurrencyRatesCacheOptions> options, ILogger<RedisCurrencyRatesCache> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _redis = redis;
        _timeToLive = options.Value.TimeToLive;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, decimal>?> GetAsync()
    {
        HashEntry[] entries;

        try
        {
            entries = await _redis.GetDatabase().HashGetAllAsync(Key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            _logger.LogWarning(ex, "Currency rates cache is unavailable; reading rates from the database.");
            return null;
        }

        if (entries.Length == 0)
        {
            return null;
        }

        var rates = new Dictionary<string, decimal>(entries.Length, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!decimal.TryParse(entry.Value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var rate))
            {
                // Treat the whole snapshot as missing rather than serving a partial one; the next sync overwrites it
                _logger.LogWarning("Currency rates cache holds an unreadable rate for {CurrencyCode}; reading rates from the database.", entry.Name.ToString());
                return null;
            }

            rates[entry.Name.ToString()] = rate;
        }

        return rates;
    }

    public Task<bool> ReplaceAsync(IReadOnlyDictionary<string, decimal> rates)
    {
        var entries = ToHashEntries(rates);
        var transaction = _redis.GetDatabase().CreateTransaction();

        // Delete first so currencies missing from the new snapshot do not linger
        _ = transaction.KeyDeleteAsync(Key);
        _ = transaction.HashSetAsync(Key, entries);
        _ = transaction.KeyExpireAsync(Key, _timeToLive);

        return ExecuteAsync(transaction);
    }

    public Task<bool> AddIfMissingAsync(IReadOnlyDictionary<string, decimal> rates)
    {
        var entries = ToHashEntries(rates);
        var transaction = _redis.GetDatabase().CreateTransaction();

        // WATCH-based condition: the transaction is discarded if another node wrote the key first
        transaction.AddCondition(Condition.KeyNotExists(Key));
        _ = transaction.HashSetAsync(Key, entries);
        _ = transaction.KeyExpireAsync(Key, _timeToLive);

        return ExecuteAsync(transaction);
    }

    private async Task<bool> ExecuteAsync(ITransaction transaction)
    {
        try
        {
            return await transaction.ExecuteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            _logger.LogWarning(ex, "Currency rates cache is unavailable; the cached snapshot was not updated.");
            return false;
        }
    }

    private static HashEntry[] ToHashEntries(IReadOnlyDictionary<string, decimal> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);

        // Redis cannot store an empty hash; an empty snapshot is a caller bug, not a cache state
        if (rates.Count == 0)
        {
            throw new ArgumentException("A currency rates snapshot must contain at least one rate.", nameof(rates));
        }

        return rates
            .Select(rate => new HashEntry(rate.Key.ToUpperInvariant(), rate.Value.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
    }
}
