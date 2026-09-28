using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Service.Interfaces;

public interface ICurrencyRatesProvider
{
    /// <summary>
    /// Returns the latest exchange rate per currency, relative to EUR. Served from the cache; the database is only
    /// queried when the cache is empty or unavailable.
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal>> GetLatestRatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reloads the latest rates from the database into the cache. Returns <c>false</c> when there was nothing to
    /// cache or the cache is unavailable.
    /// </summary>
    Task<bool> RefreshCacheAsync(CancellationToken cancellationToken = default);
}
