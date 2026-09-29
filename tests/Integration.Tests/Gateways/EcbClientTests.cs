using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Ecb.Gateway;
using Ecb.Gateway.Models;

using FluentAssertions;

using Microsoft.Extensions.Options;

namespace Integration.Tests.Gateways;

/// <summary>
/// Parses canned ECB responses through a stubbed HTTP handler, so the client is tested against the real feed format
/// without depending on the network.
/// </summary>
public sealed class EcbClientTests
{
    private static readonly DateOnly FeedDate = new(2026, 9, 25);

    [Fact]
    public async Task ParsesTheDateAndEveryRateOfTheDailyFeed()
    {
        var gateway = CreateGateway(Feed("2026-09-25", """
            <Cube currency="USD" rate="1.1403"/>
            <Cube currency="JPY" rate="171.23"/>
            <Cube currency="GBP" rate="0.86045"/>
            """));

        var daily = await gateway.GetDailyRatesAsync();

        // The feed quotes currencies against the euro and doesn't list EUR; the library reports only what it publishes
        daily.Date.Should().Be(FeedDate);
        daily.Rates.Should().Equal(new EcbRate("USD", 1.1403m), new EcbRate("JPY", 171.23m), new EcbRate("GBP", 0.86045m));
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

        var daily = await gateway.GetDailyRatesAsync();

        daily.Rates.Select(r => r.Currency).Should().Equal("USD");
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

            var daily = await gateway.GetDailyRatesAsync();

            daily.Rates.Single().Rate.Should().Be(1.1403m);
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

        var fetch = () => gateway.GetDailyRatesAsync();

        await fetch.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task FailsWhenTheFeedHasNoDatedRates()
    {
        var gateway = CreateGateway("""
            <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
              <Cube/>
            </gesmes:Envelope>
            """);

        var fetch = () => gateway.GetDailyRatesAsync();

        await fetch.Should().ThrowAsync<FormatException>();
    }

    [Fact]
    public async Task RequestsTheConfiguredFeedUrl()
    {
        var handler = new StubHandler(Feed("2026-09-25", """<Cube currency="USD" rate="1.1403"/>"""), HttpStatusCode.OK);

        await new EcbClient(new HttpClient(handler), Options.Create(new EcbClientOptions { DailyRatesUrl = FeedUrl })).GetDailyRatesAsync();

        handler.RequestedUri.Should().Be(FeedUrl);
    }

    private static readonly Uri FeedUrl = new("https://ecb.example/eurofxref-daily.xml");

    private static EcbClient CreateGateway(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new StubHandler(body, status)), Options.Create(new EcbClientOptions { DailyRatesUrl = FeedUrl }));

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
        public Uri? RequestedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            });
        }
    }
}
