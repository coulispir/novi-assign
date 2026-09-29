using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using StackExchange.Redis;

namespace App.Host.Infrastructure.HealthChecks;

/// <summary>
/// Sends a <c>PING</c> over the shared connection. While Redis is disconnected, commands fail immediately
/// (<c>BacklogPolicy.FailFast</c>), so an outage is reported at once rather than after a timeout.
/// </summary>
internal sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisHealthCheck(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // PingAsync takes no token; WaitAsync lets the registration's timeout end the wait
        await _redis.GetDatabase().PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

        return HealthCheckResult.Healthy();
    }
}
