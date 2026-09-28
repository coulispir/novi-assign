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

public sealed class EcbRatesServiceTests
{
    private static readonly DateTime RateDate = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);

    private readonly IEcbGateway _gateway = Substitute.For<IEcbGateway>();
    private readonly ICurrencyValueRepository _repository = Substitute.For<ICurrencyValueRepository>();
    private readonly EcbRatesService _service;

    private List<CurrencyValue> _added = [];

    public EcbRatesServiceTests()
    {
        _service = new EcbRatesService(_gateway, _repository, NullLogger<EcbRatesService>.Instance);

        _repository.WhenForAnyArgs(r => r.AddRange(default!)).Do(call => _added = call.Arg<IEnumerable<CurrencyValue>>().ToList());
    }

    [Fact]
    public async Task SyncLatestRatesAsync_WithNewRates_InsertsThemAndSaves()
    {
        ArrangeFetched(("USD", 1.10m), ("GBP", 0.85m));
        ArrangeStored();

        var summary = await _service.SyncLatestRatesAsync();

        summary.Should().Be(new EcbSyncSummary(Fetched: 2, Inserted: 2, Updated: 0));
        _added.Select(c => (c.CurrencyCode, c.Rate)).Should().BeEquivalentTo([("USD", 1.10m), ("GBP", 0.85m)]);
        await _repository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncLatestRatesAsync_WithAChangedRateForTheSameDate_UpdatesTheStoredRow()
    {
        var stored = CurrencyValue.Create("USD", 1.10m, RateDate);
        ArrangeFetched(("USD", 1.12m));
        ArrangeStored(stored);

        var summary = await _service.SyncLatestRatesAsync();

        summary.Should().Be(new EcbSyncSummary(Fetched: 1, Inserted: 0, Updated: 1));
        stored.Rate.Should().Be(1.12m);
        _added.Should().BeEmpty();
        await _repository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncLatestRatesAsync_WithUnchangedRates_DoesNotSave()
    {
        ArrangeFetched(("USD", 1.10m));
        ArrangeStored(CurrencyValue.Create("USD", 1.10m, RateDate));

        var summary = await _service.SyncLatestRatesAsync();

        summary.Should().Be(new EcbSyncSummary(Fetched: 1, Inserted: 0, Updated: 0));
        await _repository.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task SyncLatestRatesAsync_WithDuplicateCurrenciesInThePayload_InsertsEachOnce()
    {
        // Lower-case duplicates would otherwise hit the (CurrencyCode, RateDate) unique index on save
        ArrangeFetched(("USD", 1.10m), ("usd", 1.10m));
        ArrangeStored();

        var summary = await _service.SyncLatestRatesAsync();

        summary.Inserted.Should().Be(1);
        _added.Should().ContainSingle().Which.CurrencyCode.Should().Be("USD");
    }

    [Fact]
    public async Task SyncLatestRatesAsync_WithAnEmptyPayload_SkipsTheDatabase()
    {
        ArrangeFetched();

        var summary = await _service.SyncLatestRatesAsync();

        summary.Should().Be(new EcbSyncSummary(0, 0, 0));
        await _repository.DidNotReceiveWithAnyArgs().GetByRateDatesAsync(default!, default);
        await _repository.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    private void ArrangeFetched(params (string Currency, decimal Rate)[] rates)
    {
        _gateway.FetchDailyRatesAsync(Arg.Any<CancellationToken>())
            .Returns(rates.Select(r => new EcbRateResult(r.Currency, r.Rate, RateDate)).ToList());
    }

    private void ArrangeStored(params CurrencyValue[] stored)
    {
        _repository.GetByRateDatesAsync(Arg.Any<IReadOnlyCollection<DateTime>>(), Arg.Any<CancellationToken>())
            .Returns(stored.ToList());
    }
}
