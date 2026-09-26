using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Core.Service.Interfaces;
using Core.Service.Models;

namespace Ecb.Gateway.Services;

public class EcbGateway : IEcbGateway
{
    private readonly HttpClient _httpClient;
    private const string EcbUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private static readonly XNamespace Namespace = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";

    public EcbGateway(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<IEnumerable<EcbRateResult>> FetchDailyRatesAsync(CancellationToken cancellationToken = default)
    {
        // 1. Stream the raw byte network payload to keep execution thread allocations minimal
        using var response = await _httpClient.GetAsync(EcbUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // 2. Parse into a memory document tree natively
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var results = new List<EcbRateResult>();

        // The ECB XML envelopes place currency pairs inside nested 'Cube' elements.
        // Example parent layout block: <Cube time="2026-09-23">
        var timeCube = document.Descendants(Namespace + "Cube").First(x => x.Attribute("time") != null);
        var dateString = timeCube.Attribute("time")?.Value;

        if (!DateTime.TryParseExact(dateString, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rateDate))
        {
            rateDate = DateTime.UtcNow.Date;
        }

        // 3. Extract the inner elements: <Cube currency="USD" rate="1.1472"/>
        var currencyCubes = timeCube.Elements(Namespace + "Cube");

        foreach (var cube in currencyCubes)
        {
            var currency = cube.Attribute("currency")?.Value;
            var rateValue = cube.Attribute("rate")?.Value;

            if (string.IsNullOrWhiteSpace(currency) || string.IsNullOrWhiteSpace(rateValue))
                continue;

            if (decimal.TryParse(rateValue, CultureInfo.InvariantCulture, out var rate))
            {
                results.Add(new EcbRateResult(
                    CurrencyCode: currency.ToUpperInvariant(),
                    Rate: rate,
                    RateDate: rateDate
                ));
            }
        }

        // 4. Force inject Euro (EUR) as our base currency unit anchor (always 1:1)
        results.Add(new EcbRateResult("EUR", 1.0m, rateDate));

        return results;
    }
}
