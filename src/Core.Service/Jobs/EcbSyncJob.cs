using System;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Interfaces;

using Microsoft.Extensions.Logging;

using Quartz;

namespace Core.Service.Jobs;

[DisallowConcurrentExecution] // Prevents a new job from starting if the previous one is still running
public class EcbSyncJob : IJob
{
    public static readonly JobKey Key = new(nameof(EcbSyncJob));

    private readonly IEcbRatesService _ecbRatesService;
    private readonly ILogger<EcbSyncJob> _logger;

    public EcbSyncJob(IEcbRatesService ecbRatesService, ILogger<EcbSyncJob> logger)
    {
        _ecbRatesService = ecbRatesService;
        _logger = logger;
    }

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting automated European Central Bank exchange rate sync...");

        try
        {
            var summary = await _ecbRatesService.SyncLatestRatesAsync(cancellationToken);

            _logger.LogInformation(
                "ECB exchange rate sync completed. Fetched: {Fetched}, Inserted: {Inserted}, Updated: {Updated}.",
                summary.Fetched, summary.Inserted, summary.Updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while executing the background exchange rate synchronization loop.");
            throw new JobExecutionException(ex) { RefireImmediately = false };
        }
    }
}
