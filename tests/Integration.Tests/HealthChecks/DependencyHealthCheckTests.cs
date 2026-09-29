using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using App.Host.Infrastructure;
using App.Host.Infrastructure.HealthChecks;

using FluentAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Integration.Tests.HealthChecks;

/// <summary>
/// The health endpoints with the production registrations, against the shared Redis container. SQL Server isn't
/// running here, which covers the "database down" case; the functional tests cover a fully healthy app.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class DependencyHealthCheckTests
{
    // Nothing listens on port 1, so connecting fails straight away instead of waiting for a timeout
    private const string UnreachableSqlServer = "Server=tcp:127.0.0.1,1;Database=Health;Connect Timeout=2;Encrypt=False";
    private const string UnreachableRedis = "127.0.0.1:1,connectTimeout=500";

    private readonly RedisFixture _redis;

    public DependencyHealthCheckTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [Fact]
    public async Task LivenessIsHealthyWithoutCheckingAnyDependency()
    {
        await using var app = await StartAsync(UnreachableRedis, UnreachableSqlServer);

        var (status, body) = await GetAsync(app, DependencyHealthCheckExtensions.LivenessPath);

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("status").GetString().Should().Be("Healthy");
        body.GetProperty("checks").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task ReadinessIsUnhealthyWith503WhenSqlServerIsUnreachable()
    {
        await using var app = await StartAsync(_redis.ConnectionString, UnreachableSqlServer);

        var (status, body) = await GetAsync(app, DependencyHealthCheckExtensions.ReadinessPath);

        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.GetProperty("status").GetString().Should().Be("Unhealthy");
        body.GetProperty("checks").GetProperty(DependencyHealthCheckExtensions.SqlServerCheck).GetProperty("status").GetString().Should().Be("Unhealthy");
        body.GetProperty("checks").GetProperty(DependencyHealthCheckExtensions.RedisCheck).GetProperty("status").GetString().Should().Be("Healthy");
    }

    [Fact]
    public async Task ReportsARedisOutageAsDegradedBecauseTheAppFailsOpen()
    {
        await using var app = await StartAsync(UnreachableRedis, UnreachableSqlServer);
        var healthChecks = app.Services.GetRequiredService<HealthCheckService>();

        var report = await healthChecks.CheckHealthAsync(registration => registration.Name == DependencyHealthCheckExtensions.RedisCheck);

        report.Entries[DependencyHealthCheckExtensions.RedisCheck].Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task NeverRevealsConnectionDetailsOrExceptions()
    {
        await using var app = await StartAsync(UnreachableRedis, UnreachableSqlServer);

        using var response = await app.GetTestClient().GetAsync(new Uri(DependencyHealthCheckExtensions.ReadinessPath, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();

        text.Should().NotContain("127.0.0.1").And.NotContain("Exception").And.NotContain("description");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00:00:00")]
    public void RejectsAMissingOrNonPositiveTimeout(string? timeout)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{DependencyHealthCheckOptions.SectionName}:Timeout"] = timeout })
            .Build();

        var register = () => new ServiceCollection().AddDependencyHealthChecks(configuration, UnreachableSqlServer);

        register.Should().Throw<InvalidOperationException>().WithMessage($"*'{DependencyHealthCheckOptions.SectionName}:Timeout'*");
    }

    private static async Task<WebApplication> StartAsync(string redisConnectionString, string sqlConnectionString)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = redisConnectionString,
            [$"{DependencyHealthCheckOptions.SectionName}:Timeout"] = "00:00:05",
        });

        builder.Services.AddRedis(builder.Configuration);
        builder.Services.AddDependencyHealthChecks(builder.Configuration, sqlConnectionString);

        var app = builder.Build();
        app.MapDependencyHealthChecks();
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(WebApplication app, string path)
    {
        using var response = await app.GetTestClient().GetAsync(new Uri(path, UriKind.Relative));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }
}
