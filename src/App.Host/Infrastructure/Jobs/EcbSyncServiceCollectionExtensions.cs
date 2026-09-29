using System;

using Core.Service.Jobs;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Quartz;

namespace App.Host.Infrastructure.Jobs;

public static class EcbSyncServiceCollectionExtensions
{
    /// <summary>
    /// Schedules <see cref="EcbSyncJob"/> to run on startup and then every <c>EcbSync:Interval</c>, on a Quartz cluster
    /// backed by SQL Server so that each run happens on exactly one node.
    /// </summary>
    public static IServiceCollection AddEcbSyncJob(this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        // The trigger is built while services are being registered, before options validation can run, so the
        // interval is validated here and the host fails before it starts (as connection strings are)
        var sync = configuration.GetSection(EcbSyncOptions.SectionName).Get<EcbSyncOptions>();
        if (sync is null || !sync.IsValid())
            throw new InvalidOperationException($"'{EcbSyncOptions.SectionName}:Interval' must be configured and greater than 00:00:00.");

        services.AddQuartz(q =>
        {
            // Nodes join the same cluster by sharing the (default) scheduler name; each needs a unique instance id
            q.UseInstanceIdGenerator<UniqueNodeInstanceIdGenerator>();

            // Clustered SQL Server job store: each trigger fire is acquired by exactly one node, and
            // [DisallowConcurrentExecution] is enforced cluster-wide. If a node dies, another one takes over.
            q.UsePersistentStore(store =>
            {
                store.UseSqlServer(connectionString);
                store.UseSystemTextJsonSerializer();
                store.UseClustering();
                store.ProvisionSchema(); // Creates the QRTZ_* tables on first startup if they are missing
            });

            q.AddJob<EcbSyncJob>(opts => opts.WithIdentity(EcbSyncJob.Key));

            // The trigger is stored in the job store; Quartz overwrites it on startup (OverwriteExistingData defaults
            // to true), so a changed interval takes effect when the nodes restart
            q.AddTrigger(opts => opts
                .ForJob(EcbSyncJob.Key)
                .WithIdentity($"{nameof(EcbSyncJob)}-trigger")
                .StartNow()
                .WithSimpleSchedule(schedule => schedule
                    .WithInterval(sync.Interval)
                    .RepeatForever()));
        });

        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        return services;
    }
}
