using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Interfaces;
using Core.Service.Models;

using Ecb.Gateway;
using Ecb.Gateway.Models;

namespace App.Host.Infrastructure.Ecb;

/// <summary>
/// Adapts the standalone <see cref="IEcbClient"/> library to the core's <see cref="IEcbGateway"/> port, so neither
/// depends on the other: the gateway library knows nothing about this application, and the core knows nothing
/// about the ECB feed format.
/// </summary>
public sealed class EcbGatewayAdapter : IEcbGateway
{
    private readonly IEcbClient _client;

    public EcbGatewayAdapter(IEcbClient client)
    {
        _client = client;
    }

    public async Task<IEnumerable<EcbRateResult>> FetchDailyRatesAsync(CancellationToken cancellationToken = default)
    {
        var daily = await _client.GetDailyRatesAsync(cancellationToken).ConfigureAwait(false);
        var rateDate = daily.Date.ToDateTime(TimeOnly.MinValue);

        // The feed quotes every currency against the euro, so EUR itself is 1. The core converts through it and
        // stores it like any other currency.
        return daily.Rates
            .Select(rate => new EcbRateResult(rate.Currency, rate.Rate, rateDate))
            .Append(new EcbRateResult(EcbDailyRates.BaseCurrency, 1.0m, rateDate))
            .ToList();
    }
}
