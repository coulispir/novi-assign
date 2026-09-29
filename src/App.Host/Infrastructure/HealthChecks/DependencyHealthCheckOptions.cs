using System;

namespace App.Host.Infrastructure.HealthChecks;

/// <summary>
/// Settings for the dependency health checks, bound from <c>HealthChecks</c>.
/// </summary>
public sealed class DependencyHealthCheckOptions
{
    public const string SectionName = "HealthChecks";

    // No default on purpose: a missing value fails at startup. Keep it below the load balancer's probe timeout.
    public TimeSpan Timeout { get; init; }

    internal bool IsValid() => Timeout > TimeSpan.Zero;
}
