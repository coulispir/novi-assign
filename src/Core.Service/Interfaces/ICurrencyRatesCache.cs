using System.Collections.Generic;
using System.Threading.Tasks;

namespace Core.Service.Interfaces;

/// <summary>
/// Shared cache holding one snapshot of the latest exchange rate per currency, relative to EUR.
/// Implementations never throw when the underlying store is unavailable: reads report a miss and writes report
/// <c>false</c>, so callers fall back to the database instead of failing the request.
/// </summary>
public interface ICurrencyRatesCache
{
    /// <summary>
    /// Returns the cached snapshot, or <c>null</c> when none is cached or the cache cannot be read.
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal>?> GetAsync();

    /// <summary>
    /// Atomically replaces the cached snapshot, dropping currencies that are no longer present.
    /// Returns <c>false</c> when the cache is unavailable.
    /// </summary>
    Task<bool> ReplaceAsync(IReadOnlyDictionary<string, decimal> rates);

    /// <summary>
    /// Stores the snapshot only if none is cached, so a read-through fill can never overwrite a fresher snapshot
    /// written by the sync job in the meantime. Returns <c>false</c> when a snapshot already exists or the cache is unavailable.
    /// </summary>
    Task<bool> AddIfMissingAsync(IReadOnlyDictionary<string, decimal> rates);
}
