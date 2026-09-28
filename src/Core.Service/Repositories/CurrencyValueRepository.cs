using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Data;
using Core.Service.Entities;
using Core.Service.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace Core.Service.Repositories;

public class CurrencyValueRepository : ICurrencyValueRepository
{
    private readonly SystemDbContext _dbContext;

    public CurrencyValueRepository(SystemDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<List<CurrencyValue>> GetByRateDatesAsync(IReadOnlyCollection<DateTime> rateDates, CancellationToken cancellationToken = default)
    {
        return _dbContext.CurrencyValues
            .Where(c => rateDates.Contains(c.RateDate))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetLatestRatesAsync(CancellationToken cancellationToken = default)
    {
        // A currency the ECB stops publishing keeps its last known rate, so pick the latest date per currency
        // rather than the latest date overall. Served by the (CurrencyCode, RateDate) unique index.
        var latestRates = await _dbContext.CurrencyValues
            .AsNoTracking()
            .Where(c => c.RateDate == _dbContext.CurrencyValues
                .Where(other => other.CurrencyCode == c.CurrencyCode)
                .Max(other => other.RateDate))
            .Select(c => new { c.CurrencyCode, c.Rate })
            .ToListAsync(cancellationToken);

        return latestRates.ToDictionary(c => c.CurrencyCode, c => c.Rate, StringComparer.OrdinalIgnoreCase);
    }

    public void AddRange(IEnumerable<CurrencyValue> currencyValues)
    {
        _dbContext.CurrencyValues.AddRange(currencyValues);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }
}
