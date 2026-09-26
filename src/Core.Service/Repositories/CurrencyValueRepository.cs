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

    public void AddRange(IEnumerable<CurrencyValue> currencyValues)
    {
        _dbContext.CurrencyValues.AddRange(currencyValues);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }
}
