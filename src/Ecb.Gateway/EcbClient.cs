using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Ecb.Gateway.Models;

using Microsoft.Extensions.Options;

namespace Ecb.Gateway;

public class EcbClient : IEcbClient
{
    private static readonly XNamespace Namespace = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";

    private readonly HttpClient _httpClient;
    private readonly Uri _dailyRatesUrl;

    public EcbClient(HttpClient httpClient, IOptions<EcbClientOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _dailyRatesUrl = options.Value.DailyRatesUrl
            ?? throw new ArgumentException($"{nameof(EcbClientOptions.DailyRatesUrl)} must be configured.", nameof(options));
    }

    public async Task<EcbDailyRates> GetDailyRatesAsync(CancellationToken cancellationToken = default)
    {
        // Stream the response rather than buffering it as a string
        using var response = await _httpClient.GetAsync(_dailyRatesUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        // The rates are nested in a dated Cube: <Cube time="2026-09-23"><Cube currency="USD" rate="1.1472"/>...</Cube>
        var timeCube = document.Descendants(Namespace + "Cube").FirstOrDefault(cube => cube.Attribute("time") is not null)
            ?? throw new FormatException("The ECB feed contains no dated <Cube time=\"...\"> element.");

        if (!DateOnly.TryParseExact(timeCube.Attribute("time")!.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow);
        }

        var rates = new List<EcbRate>();

        foreach (var cube in timeCube.Elements(Namespace + "Cube"))
        {
            var currency = cube.Attribute("currency")?.Value;
            var rateValue = cube.Attribute("rate")?.Value;

            // Skip malformed entries rather than failing the whole feed
            if (string.IsNullOrWhiteSpace(currency) || !decimal.TryParse(rateValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate))
                continue;

            rates.Add(new EcbRate(currency.ToUpperInvariant(), rate));
        }

        return new EcbDailyRates(date, rates);
    }
}
