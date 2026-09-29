using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;
using Core.Service.Models;

namespace Core.Service.Interfaces;

public interface ICurrencyValueRepository
{
    /// <summary>
    /// Loads the most recent rate recorded for each currency, keyed case-insensitively by currency code.
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal>> GetLatestRatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the rates in one SQL <c>MERGE</c> statement, keyed on (CurrencyCode, RateDate): a rate recorded for a
    /// date is updated if it changed, and a missing date is inserted. The statement is atomic, so either every rate is
    /// saved or none is. Each (CurrencyCode, RateDate) must appear at most once in <paramref name="rates"/>.
    /// </summary>
    Task<CurrencyRatesMergeResult> MergeRatesAsync(IReadOnlyCollection<CurrencyValue> rates, CancellationToken cancellationToken = default);
}
