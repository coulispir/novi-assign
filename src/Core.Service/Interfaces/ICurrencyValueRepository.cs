using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;

namespace Core.Service.Interfaces;

public interface ICurrencyValueRepository
{
    /// <summary>
    /// Loads the tracked currency rows recorded for any of the given ECB rate dates.
    /// </summary>
    Task<List<CurrencyValue>> GetByRateDatesAsync(IReadOnlyCollection<DateTime> rateDates, CancellationToken cancellationToken = default);

    void AddRange(IEnumerable<CurrencyValue> currencyValues);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
