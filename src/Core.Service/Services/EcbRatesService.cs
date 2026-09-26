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

        // Load existing rows for the same rate dates so we can upsert against the (CurrencyCode, RateDate) key
        var rateDates = fetchedRates.Select(r => r.RateDate.Date).Distinct().ToList();
        var existingRates = await _currencyValueRepository.GetByRateDatesAsync(rateDates, cancellationToken);
        var existingByKey = existingRates.ToDictionary(c => (c.CurrencyCode, c.RateDate.Date));

        var newRates = new List<CurrencyValue>();
        var updated = 0;

        foreach (var rate in fetchedRates)
        {
            var key = (rate.CurrencyCode.ToUpperInvariant(), rate.RateDate.Date);

            if (existingByKey.TryGetValue(key, out var existing))
            {
                if (existing.Rate != rate.Rate)
                {
                    existing.UpdateRate(rate.Rate, rate.RateDate);
                    updated++;
                }

                continue;
            }

            var currencyValue = CurrencyValue.Create(rate.CurrencyCode, rate.Rate, rate.RateDate);
            existingByKey[key] = currencyValue; // Guards against duplicate entries within the same payload
            newRates.Add(currencyValue);
        }

        if (newRates.Count > 0 || updated > 0)
        {
            _currencyValueRepository.AddRange(newRates);
            await _currencyValueRepository.SaveChangesAsync(cancellationToken);
        }

        return new EcbSyncSummary(fetchedRates.Count, newRates.Count, updated);
    }
}
