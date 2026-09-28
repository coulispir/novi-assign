using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Interfaces;

using Microsoft.Extensions.Logging;

namespace Core.Service.Services;

public class CurrencyRatesProvider : ICurrencyRatesProvider
{
    private readonly ICurrencyRatesCache _cache;
    private readonly ICurrencyValueRepository _currencyValueRepository;
    private readonly ILogger<CurrencyRatesProvider> _logger;

    public CurrencyRatesProvider(ICurrencyRatesCache cache, ICurrencyValueRepository currencyValueRepository, ILogger<CurrencyRatesProvider> logger)
    {
        _cache = cache;
        _currencyValueRepository = currencyValueRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetLatestRatesAsync(CancellationToken cancellationToken = default)
    {
        var cachedRates = await _cache.GetAsync();
        if (cachedRates is not null) return cachedRates;

        var rates = await _currencyValueRepository.GetLatestRatesAsync(cancellationToken);

        // Fill only an empty cache: if the sync job refreshed it while we were reading the database, its snapshot wins
        if (rates.Count > 0)
            await _cache.AddIfMissingAsync(rates);

        return rates;
    }

    public async Task<bool> RefreshCacheAsync(CancellationToken cancellationToken = default)
    {
        var rates = await _currencyValueRepository.GetLatestRatesAsync(cancellationToken);

        if (rates.Count == 0)
        {
            _logger.LogWarning("No currency rates are stored yet. Skipping currency rates cache refresh.");
            return false;
        }

        return await _cache.ReplaceAsync(rates);
    }
}
