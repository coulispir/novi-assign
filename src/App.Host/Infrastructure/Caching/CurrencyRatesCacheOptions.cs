using System;

namespace App.Host.Infrastructure.Caching;

/// <summary>
/// Settings for the shared currency rates cache, bound from <c>CurrencyRatesCache</c>.
/// </summary>
public sealed class CurrencyRatesCacheOptions
{
    public const string SectionName = "CurrencyRatesCache";

    // No default on purpose: a missing value fails validation at startup.
    // A safety net only: the sync job rewrites the snapshot on every run, so this must be longer than its interval.
    // It also keeps the key evictable under volatile-* maxmemory policies.
    public TimeSpan TimeToLive { get; init; }

    internal bool IsValid() => TimeToLive > TimeSpan.Zero;
}
