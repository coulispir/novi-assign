using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Interfaces;
using Core.Service.Models;

namespace Functional.Tests.Infrastructure;

/// <summary>
/// Stands in for the ECB feed, the only external dependency the functional tests do not run for real.
/// Tests publish a new day of rates and then run the sync job, the same way production picks up the daily feed.
/// </summary>
public sealed class FakeEcbFeed : IEcbGateway
{
    // Never changed by any test, so conversions between them are stable across the whole run
    public const string StableCurrencyA = "USD";
    public const string StableCurrencyB = "GBP";

    // Each test that changes the feed or the cache owns one currency, so tests stay independent of their order
    public const string CurrencyChangedBySync = "CHF";
    public const string CurrencyDroppedFromFeed = "NOK";
    public const string CurrencyOverriddenInCache = "SEK";

    public static readonly IReadOnlyDictionary<string, decimal> InitialRates = new Dictionary<string, decimal>
    {
        [StableCurrencyA] = 1.1403m,
        [StableCurrencyB] = 0.86045m,
        [CurrencyChangedBySync] = 0.9361m,
        [CurrencyDroppedFromFeed] = 11.7215m,
        [CurrencyOverriddenInCache] = 11.0385m,
    };

    private DateTime _date = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private IReadOnlyList<EcbRateResult> _published = [];

    public FakeEcbFeed()
    {
        PublishNextDay(InitialRates);
    }

    /// <summary>
    /// Publishes the given rates for the day after the previous publication. Always moving forward keeps "latest rate
    /// per currency" deterministic no matter which tests ran before.
    /// </summary>
    public void PublishNextDay(IReadOnlyDictionary<string, decimal> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);

        _date = _date.AddDays(1);
        _published = rates
            .Select(rate => new EcbRateResult(rate.Key, rate.Value, _date))
            .Append(new EcbRateResult("EUR", 1.0m, _date))
            .ToList();
    }

    public Task<IEnumerable<EcbRateResult>> FetchDailyRatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IEnumerable<EcbRateResult>>(_published);
}
