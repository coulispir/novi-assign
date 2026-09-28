using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Interfaces;
using Core.Service.Jobs;
using Core.Service.Models;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Quartz;

namespace Unit.Tests.Application;

public sealed class EcbSyncJobTests
{
    private readonly IEcbRatesService _ecbRatesService = Substitute.For<IEcbRatesService>();
    private readonly ICurrencyRatesProvider _currencyRatesProvider = Substitute.For<ICurrencyRatesProvider>();
    private readonly EcbSyncJob _job;

    public EcbSyncJobTests()
    {
        _job = new EcbSyncJob(_ecbRatesService, _currencyRatesProvider, NullLogger<EcbSyncJob>.Instance);
    }

    [Fact]
    public async Task Execute_AfterSuccessfulSync_RefreshesTheCache()
    {
        _ecbRatesService.SyncLatestRatesAsync(Arg.Any<CancellationToken>()).Returns(new EcbSyncSummary(31, 0, 0));

        await _job.Execute(Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        Received.InOrder(() =>
        {
            _ecbRatesService.SyncLatestRatesAsync(Arg.Any<CancellationToken>());
            _currencyRatesProvider.RefreshCacheAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Execute_WhenSyncFails_LeavesTheCacheUntouched()
    {
        _ecbRatesService.SyncLatestRatesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("ECB is down"));

        var execute = async () => await _job.Execute(Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        await execute.Should().ThrowAsync<JobExecutionException>();
        await _currencyRatesProvider.DidNotReceiveWithAnyArgs().RefreshCacheAsync(default);
    }
}
