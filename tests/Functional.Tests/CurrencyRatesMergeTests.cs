using System;
using System.Linq;
using System.Threading.Tasks;

using Core.Service.Data;
using Core.Service.Entities;
using Core.Service.Interfaces;
using Core.Service.Models;

using FluentAssertions;

using Functional.Tests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Functional.Tests;

/// <summary>
/// The raw SQL MERGE that persists ECB rates, against real SQL Server. Each test owns its currency codes, on dates the
/// fake ECB feed never publishes, so it can't collide with other tests' rates.
/// </summary>
[Collection(WalletApiCollection.Name)]
public sealed class CurrencyRatesMergeTests
{
    private static readonly DateTime Day1 = new(2000, 1, 3, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Day2 = Day1.AddDays(1);

    private readonly WalletApiFactory _factory;

    public CurrencyRatesMergeTests(WalletApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task InsertsMissingDatesAndUpdatesOnlyTheRatesThatChanged()
    {
        var first = await MergeAsync(CurrencyValue.Create("MGA", 1.1m, Day1), CurrencyValue.Create("MGB", 2.2m, Day1));
        first.Should().Be(new CurrencyRatesMergeResult(Inserted: 2, Updated: 0));
        var unchangedRowStampedAt = (await StoredAsync("MGA")).Single().UpdatedAt;

        var second = await MergeAsync(
            CurrencyValue.Create("MGA", 1.1m, Day1),   // unchanged
            CurrencyValue.Create("MGB", 2.3m, Day1),   // changed rate for an existing date
            CurrencyValue.Create("MGA", 1.2m, Day2));  // new date

        second.Should().Be(new CurrencyRatesMergeResult(Inserted: 1, Updated: 1));

        var a = await StoredAsync("MGA");
        a.Select(r => (r.RateDate, r.Rate)).Should().Equal((Day1, 1.1m), (Day2, 1.2m)); // history is kept per date
        a[0].UpdatedAt.Should().Be(unchangedRowStampedAt); // an unchanged rate isn't rewritten
        (await StoredAsync("MGB")).Should().ContainSingle().Which.Rate.Should().Be(2.3m);
    }

    [Fact]
    public async Task MergingTheSameRatesAgainChangesNothing()
    {
        var rates = new[] { CurrencyValue.Create("MGC", 1.5m, Day1), CurrencyValue.Create("MGD", 0.5m, Day1) };

        await MergeAsync(rates);
        var again = await MergeAsync(rates);

        again.Should().Be(new CurrencyRatesMergeResult(Inserted: 0, Updated: 0));
        (await StoredAsync("MGC")).Should().ContainSingle();
    }

    [Fact]
    public async Task StoresRatesAtTheColumnsFullPrecision()
    {
        await MergeAsync(CurrencyValue.Create("MGE", 171.123456m, Day1));

        (await StoredAsync("MGE")).Single().Rate.Should().Be(171.123456m);
    }

    [Fact]
    public async Task RejectsMoreRatesThanOneStatementCanCarry()
    {
        var tooMany = Enumerable.Range(0, Core.Service.Repositories.CurrencyValueRepository.MaxRatesPerMerge + 1)
            .Select(day => CurrencyValue.Create("MGF", 1m, Day1.AddDays(day)))
            .ToArray();

        var merge = () => MergeAsync(tooMany);

        await merge.Should().ThrowAsync<ArgumentException>();
        (await StoredAsync("MGF")).Should().BeEmpty();
    }

    private async Task<CurrencyRatesMergeResult> MergeAsync(params CurrencyValue[] rates)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICurrencyValueRepository>().MergeRatesAsync(rates);
    }

    private async Task<CurrencyValue[]> StoredAsync(string currencyCode)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SystemDbContext>().CurrencyValues
            .AsNoTracking()
            .Where(c => c.CurrencyCode == currencyCode)
            .OrderBy(c => c.RateDate)
            .ToArrayAsync();
    }
}
