using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using App.Host.Infrastructure.RateLimiting;

using Core.Service.Interfaces;
using Core.Service.Jobs;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using NSubstitute;

using Quartz;

using Testcontainers.MsSql;
using Testcontainers.Redis;

using Wallet.Api;

namespace Functional.Tests.Infrastructure;

/// <summary>
/// Boots the real application (<c>Program.cs</c>: DI, middleware, migrations) in memory against real SQL Server and
/// Redis containers. Only the ECB feed is faked, and the Quartz scheduler is not started, so each test decides exactly
/// when a sync happens instead of racing a background trigger.
/// </summary>
public sealed class WalletApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string DatabaseName = "WalletFunctionalTests";

    // Same images as docker-compose.yml, so tests run against the versions the app is developed with
    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine").Build();

    public FakeEcbFeed EcbFeed { get; } = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sqlServer.StartAsync(), _redis.StartAsync());

        // Starts the host: Program.cs creates the database and applies the migrations
        _ = Server;

        await RunEcbSyncJobAsync();
    }

    /// <summary>
    /// Runs the production sync job once, as a Quartz trigger would: fetch the feed, upsert SQL Server, refresh Redis.
    /// </summary>
    public async Task RunEcbSyncJobAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var job = ActivatorUtilities.CreateInstance<EcbSyncJob>(scope.ServiceProvider);

        await job.Execute(Substitute.For<IJobExecutionContext>(), CancellationToken.None);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var sqlConnectionString = new SqlConnectionStringBuilder(_sqlServer.GetConnectionString()) { InitialCatalog = DatabaseName };

        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", sqlConnectionString.ConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());

        // Every in-memory request comes from the same (unknown) client, so production limits would throttle the suite.
        // Rate limiting itself is covered by the integration tests.
        foreach (var policy in RateLimitPolicies.All)
        {
            builder.UseSetting($"{RateLimitPolicyOptions.SectionName}:{policy}:PermitLimit", "100000");
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEcbGateway>();
            services.AddSingleton<IEcbGateway>(EcbFeed);

            var quartzHostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(QuartzHostedService))
                .ToList();

            if (quartzHostedServices.Count == 0)
            {
                throw new InvalidOperationException("Quartz hosted service registration not found; the scheduler would run background syncs during tests.");
            }

            foreach (var descriptor in quartzHostedServices)
            {
                services.Remove(descriptor);
            }
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await Task.WhenAll(_sqlServer.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public sealed class WalletApiCollection : ICollectionFixture<WalletApiFactory>
{
    public const string Name = "Wallet API";
}
