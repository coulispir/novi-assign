using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace App.Host.Infrastructure.RateLimiting;

/// <summary>
/// Decorates a Redis-backed limiter so a Redis outage lets requests through (logged) instead of failing every request
/// with a 500. Rate limiting protects the service; it must not become a single point of failure for it.
/// </summary>
internal sealed class FailOpenRateLimiter : RateLimiter
{
    private readonly RateLimiter _inner;
    private readonly ILogger _logger;

    public FailOpenRateLimiter(RateLimiter inner, ILogger logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public override TimeSpan? IdleDuration => _inner.IdleDuration;

    public override RateLimiterStatistics? GetStatistics() => _inner.GetStatistics();

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => _inner.AttemptAcquire(permitCount);

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        try
        {
            return await _inner.AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            _logger.LogWarning(ex, "Rate limiter store is unavailable; allowing request without rate limiting.");
            return AllowedLease.Instance;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override ValueTask DisposeAsyncCore() => _inner.DisposeAsync();

    private sealed class AllowedLease : RateLimitLease
    {
        public static readonly AllowedLease Instance = new();

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => Array.Empty<string>();

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
