using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Entities;
using Core.Service.Interfaces;
using Core.Service.Models;
using Core.Service.Services;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

namespace Unit.Tests.Application;

/// <summary>
/// What the service hands to the MERGE. The SQL itself runs against a real SQL Server in the functional tests.
/// </summary>
public sealed class EcbRatesServiceTests
{
    private static readonly DateTime RateDate = new(2026, 9, 25, 14, 30, 0, DateTimeKind.Utc);

    private readonly IEcbGateway _gateway = Substitute.For<IEcbGateway>();
    private readonly ICurrencyValueRepository _repository = Substitute.For<ICurrencyValueRepository>();
    private readonly EcbRatesService _service;

    private List<CurrencyValue> _merged = [];

    public EcbRatesServiceTests()
    {
        _service = new EcbRatesService(_gateway, _repository, NullLogger<EcbRatesService>.Instance);

        _repository.MergeRatesAsync(default!, default)
            .ReturnsForAnyArgs(call =>
            {
                _merged = call.Arg<IReadOnlyCollection<CurrencyValue>>().ToList();
                return new CurrencyRatesMergeResult(Inserted: 1, Updated: 1);
            });
    }

    [Fact]
    public async Task SyncLatestRatesAsync_MergesTheWholePayloadInOneCall()
    {
        ArrangeFetched(("USD", 1.10m), ("GBP", 0.85m));

        await _service.SyncLatestRatesAsync();

        await _repository.ReceivedWithAnyArgs(1).MergeRatesAsync(default!, default);
        _merged.Select(c => (c.CurrencyCode, c.Rate)).Should().BeEquivalentTo([("USD", 1.10m), ("GBP", 0.85m)]);
    }

    [Fact]
    public async Task SyncLatestRatesAsync_ReportsTheFetchedCountAndTheMergeCounts()
    {
        ArrangeFetched(("USD", 1.10m), ("GBP", 0.85m), ("JPY", 171.2m));

        var summary = await _service.SyncLatestRatesAsync();

        summary.Should().Be(new EcbSyncSummary(Fetched: 3, Inserted: 1, Updated: 1));
    }

    [Fact]
    public async Task SyncLatestRatesAsync_NormalizesCurrencyCodesAndRateDates()
    {
        ArrangeFetched(("usd", 1.10m));

        await _service.SyncLatestRatesAsync();

        var rate = _merged.Should().ContainSingle().Subject;
        rate.CurrencyCode.Should().Be("USD");
        rate.RateDate.Should().Be(RateDate.Date);
    }

    [Fact]
    public async Task SyncLatestRatesAsync_WithDuplicateCurrenciesInThePayload_MergesEachOnceKeepingTheLastRate()
    {
        // A MERGE fails if its source matches the same row twice
        ArrangeFetched(("USD", 1.10m), ("usd", 1.12m));

        await _service.SyncLatestRatesAsync();

        _merged.Should().ContainSingle().Which.Rate.Should().Be(1.12m);
    }

    [Fact]
    public async Task SyncLatestRatesAsync_WithAnInvalidRate_ThrowsWithoutWritingAnything()
    {
        ArrangeFetched(("USD", 1.10m), ("GBP", 0m));

        var sync = () => _service.SyncLatestRatesAsync();

        await sync.Should().ThrowAsync<ArgumentException>();
        await _repository.DidNotReceiveWithAnyArgs().MergeRatesAsync(default!, default);
    }

    [Fact]
    public async Task SyncLatestRatesAsync_WithAnEmptyPayload_SkipsTheDatabase()
    {
        ArrangeFetched();

        var summary = await _service.SyncLatestRatesAsync();

        summary.Should().Be(new EcbSyncSummary(0, 0, 0));
        await _repository.DidNotReceiveWithAnyArgs().MergeRatesAsync(default!, default);
    }

    private void ArrangeFetched(params (string Currency, decimal Rate)[] rates)
    {
        _gateway.FetchDailyRatesAsync(Arg.Any<CancellationToken>())
            .Returns(rates.Select(r => new EcbRateResult(r.Currency, r.Rate, RateDate)).ToList());
    }
}
