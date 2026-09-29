using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace App.Host.Infrastructure.HealthChecks;

public static class DependencyHealthCheckExtensions
{
    /// <summary>Readiness: the app and its dependencies. For the load balancer and readiness probes.</summary>
    public const string ReadinessPath = "/health";

    /// <summary>Liveness: the process answers, nothing else is checked. For liveness probes, so a dependency outage never restarts it.</summary>
    public const string LivenessPath = "/health/live";

    public const string SqlServerCheck = "sql-server";
    public const string RedisCheck = "redis";

    private const string DependencyTag = "dependency";

    /// <summary>
    /// Registers the SQL Server and Redis checks behind <see cref="ReadinessPath"/>. SQL Server is required, so its
    /// failure is <see cref="HealthStatus.Unhealthy"/> (503). Redis failing is only <see cref="HealthStatus.Degraded"/>
    /// (still 200): rate limiting and the rates cache fail open, so the app keeps serving, and a Redis outage must not make
    /// the load balancer pull every replica at once. Requires <see cref="RedisServiceCollectionExtensions.AddRedis"/>.
    /// </summary>
    public static IServiceCollection AddDependencyHealthChecks(this IServiceCollection services, IConfiguration configuration, string sqlConnectionString)
    {
        // Each check's timeout is fixed while services are being registered, so it is validated here rather than on start
        var options = configuration.GetSection(DependencyHealthCheckOptions.SectionName).Get<DependencyHealthCheckOptions>();
        if (options is null || !options.IsValid())
            throw new InvalidOperationException($"'{DependencyHealthCheckOptions.SectionName}:Timeout' must be configured and greater than 00:00:00.");

        services.AddHealthChecks()
            .AddCheck(SqlServerCheck, new SqlServerHealthCheck(sqlConnectionString), HealthStatus.Unhealthy, [DependencyTag], options.Timeout)
            .AddCheck<RedisHealthCheck>(RedisCheck, HealthStatus.Degraded, [DependencyTag], options.Timeout);

        return services;
    }

    public static WebApplication MapDependencyHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks(LivenessPath, new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteJsonAsync,
        });

        app.MapHealthChecks(ReadinessPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(DependencyTag),
            ResponseWriter = WriteJsonAsync,
        });

        return app;
    }

    /// <summary>
    /// Probes run every few seconds per replica; the request log would drown in them, so successful ones are logged at
    /// Verbose. Failed checks are still logged by the health check service itself.
    /// </summary>
    public static bool IsHealthCheckRequest(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Request.Path.StartsWithSegments(ReadinessPath, StringComparison.OrdinalIgnoreCase);
    }

    // Status and duration only: never descriptions or exceptions, which could reveal hosts or connection details
    private static Task WriteJsonAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = (long)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new { status = entry.Value.Status.ToString(), durationMs = (long)entry.Value.Duration.TotalMilliseconds }),
        });
}
