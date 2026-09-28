using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Models;

using Ecb.Gateway.Services;

using FluentAssertions;

namespace Integration.Tests.Gateways;

/// <summary>
/// Parses canned ECB responses through a stubbed HTTP handler, so the adapter is tested against the real feed format
/// without depending on the network.
/// </summary>
public sealed class EcbGatewayTests
{
    private static readonly DateTime FeedDate = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public async Task ParsesEveryRateFromTheDailyFeedAndAddsTheEuroBase()
    {
        var gateway = CreateGateway(Feed("2026-09-25", """
            <Cube currency="USD" rate="1.1403"/>
            <Cube currency="JPY" rate="171.23"/>
            <Cube currency="GBP" rate="0.86045"/>
            """));

        var rates = (await gateway.FetchDailyRatesAsync()).ToList();

        rates.Should().BeEquivalentTo(
        [
            new EcbRateResult("USD", 1.1403m, FeedDate),
            new EcbRateResult("JPY", 171.23m, FeedDate),
            new EcbRateResult("GBP", 0.86045m, FeedDate),
            new EcbRateResult("EUR", 1.0m, FeedDate),
        ]);
    }

    [Fact]
    public async Task SkipsEntriesWithAMissingOrUnparsableRate()
    {
        var gateway = CreateGateway(Feed("2026-09-25", """
            <Cube currency="USD" rate="1.1403"/>
            <Cube currency="JPY" rate="not-a-number"/>
            <Cube currency="GBP"/>
            <Cube rate="1.5"/>
            """));

        var rates = await gateway.FetchDailyRatesAsync();

        rates.Select(r => r.CurrencyCode).Should().BeEquivalentTo(["USD", "EUR"]);
    }

    [Fact]
    public async Task ParsesRatesIndependentlyOfTheServerCulture()
    {
        // A comma-decimal culture must not turn "1.1403" into 11403
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("el-GR");

        try
        {
            var gateway = CreateGateway(Feed("2026-09-25", """<Cube currency="USD" rate="1.1403"/>"""));

            var rates = await gateway.FetchDailyRatesAsync();

            rates.First(r => r.CurrencyCode == "USD").Rate.Should().Be(1.1403m);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task FailsWhenTheFeedReturnsAnErrorStatus()
    {
        var gateway = CreateGateway("Service Unavailable", HttpStatusCode.ServiceUnavailable);

        var fetch = () => gateway.FetchDailyRatesAsync();

        await fetch.Should().ThrowAsync<HttpRequestException>();
    }

    private static EcbGateway CreateGateway(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new StubHandler(body, status)));

    private static string Feed(string date, string currencyCubes) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
          <gesmes:subject>Reference rates</gesmes:subject>
          <Cube>
            <Cube time="{date}">
              {currencyCubes}
            </Cube>
          </Cube>
        </gesmes:Envelope>
        """;

    private sealed class StubHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            });
    }
}
