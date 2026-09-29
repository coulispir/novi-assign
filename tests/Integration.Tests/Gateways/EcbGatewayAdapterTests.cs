using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using App.Host.Infrastructure.Ecb;

using Core.Service.Models;

using Ecb.Gateway;
using Ecb.Gateway.Models;

using FluentAssertions;

using NSubstitute;

namespace Integration.Tests.Gateways;

public sealed class EcbGatewayAdapterTests
{
    private readonly IEcbClient _client = Substitute.For<IEcbClient>();
    private readonly EcbGatewayAdapter _adapter;

    public EcbGatewayAdapterTests()
    {
        _adapter = new EcbGatewayAdapter(_client);
    }

    [Fact]
    public async Task MapsEveryPublishedRateAndAddsTheEuroBase()
    {
        _client.GetDailyRatesAsync(Arg.Any<CancellationToken>())
            .Returns(new EcbDailyRates(new DateOnly(2026, 9, 25), [new EcbRate("USD", 1.1403m), new EcbRate("GBP", 0.86045m)]));

        var rates = (await _adapter.FetchDailyRatesAsync()).ToList();

        var rateDate = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Unspecified);
        rates.Should().Equal(
            new EcbRateResult("USD", 1.1403m, rateDate),
            new EcbRateResult("GBP", 0.86045m, rateDate),
            new EcbRateResult("EUR", 1.0m, rateDate));
    }

    [Fact]
    public async Task AddsTheEuroBaseEvenWhenTheFeedHasNoRates()
    {
        _client.GetDailyRatesAsync(Arg.Any<CancellationToken>()).Returns(new EcbDailyRates(new DateOnly(2026, 9, 25), []));

        var rates = await _adapter.FetchDailyRatesAsync();

        rates.Should().ContainSingle().Which.CurrencyCode.Should().Be("EUR");
    }
}
