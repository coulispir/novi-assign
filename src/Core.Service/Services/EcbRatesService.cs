using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;
using Core.Service.Interfaces;
using Core.Service.Models;

using Microsoft.Extensions.Logging;

namespace Core.Service.Services;

public class EcbRatesService : IEcbRatesService
{
    private readonly IEcbGateway _ecbGateway;
    private readonly ICurrencyValueRepository _currencyValueRepository;
    private readonly ILogger<EcbRatesService> _logger;

    public EcbRatesService(IEcbGateway ecbGateway, ICurrencyValueRepository currencyValueRepository, ILogger<EcbRatesService> logger)
    {
        _ecbGateway = ecbGateway;
        _currencyValueRepository = currencyValueRepository;
        _logger = logger;
    }

    public async Task<EcbSyncSummary> SyncLatestRatesAsync(CancellationToken cancellationToken = default)
    {
        // Fetch live rate payloads from the external XML gateway
        var fetchedRates = (await _ecbGateway.FetchDailyRatesAsync(cancellationToken)).ToList();

        if (fetchedRates.Count == 0)
        {
            _logger.LogWarning("ECB Gateway returned an empty rate list. Skipping database synchronization.");
            return new EcbSyncSummary(0, 0, 0);
        }

        // Create validates and normalises each rate (upper-case code, date only). One row per (CurrencyCode, RateDate):
        // a MERGE fails if its source matches the same target row twice, so a duplicate in the payload keeps the last rate.
        var rates = fetchedRates
            .Select(rate => CurrencyValue.Create(rate.CurrencyCode, rate.Rate, rate.RateDate))
            .GroupBy(rate => (rate.CurrencyCode, rate.RateDate))
            .Select(group => group.Last())
            .ToList();

        // One MERGE statement for the whole payload: updates changed rates and inserts missing dates in a single transaction
        var merged = await _currencyValueRepository.MergeRatesAsync(rates, cancellationToken);

        return new EcbSyncSummary(fetchedRates.Count, merged.Inserted, merged.Updated);
    }
}
