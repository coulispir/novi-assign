using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RedisRateLimiting;

using StackExchange.Redis;

namespace App.Host.Infrastructure.RateLimiting;

public static class RateLimitingServiceCollectionExtensions
{
    /// <summary>
    /// Registers one policy per name in <paramref name="policyNames"/> on top of the native ASP.NET Core rate limiting
    /// middleware, each with its own limits from <c>RateLimiting:Policies:{name}</c>. Counters live in Redis, so a limit
    /// holds across every node behind the load balancer, and each check-and-increment runs as a single atomic Lua script,
    /// so concurrent requests cannot race past the limit.
    /// </summary>
    public static IServiceCollection AddClientIpRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration,
        IReadOnlyCollection<string> policyNames)
    {
        ArgumentNullException.ThrowIfNull(policyNames);

        foreach (var policyName in policyNames)
        {
            var sectionPath = $"{RateLimitPolicyOptions.SectionName}:{policyName}";

            services.AddOptions<RateLimitPolicyOptions>(policyName)
                .Bind(configuration.GetSection(sectionPath))
                .Validate(
                    limits => limits.IsValid(),
                    $"Rate limit policy '{policyName}' must be configured under '{sectionPath}' with PermitLimit > 0 and Window >= {RateLimitPolicyOptions.MinimumWindow}.")
                .ValidateOnStart();
        }

        services.AddRateLimiter(_ => { });

        services.AddOptions<RateLimiterOptions>()
            .Configure<IConnectionMultiplexer, IOptionsMonitor<RateLimitPolicyOptions>, ILoggerFactory>((options, redis, policyOptions, loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger(typeof(RateLimitingServiceCollectionExtensions));

                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = (context, cancellationToken) => OnRejectedAsync(context, logger, cancellationToken);

                foreach (var policyName in policyNames)
                {
                    var limits = policyOptions.Get(policyName);
                    var windowOptions = new RedisFixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.PermitLimit,
                        Window = limits.Window,
                        ConnectionMultiplexerFactory = () => redis,
                    };

                    options.AddPolicy(policyName, httpContext =>
                        RateLimitPartition.Get(
                            GetPartitionKey(httpContext),
                            key => new FailOpenRateLimiter(new RedisFixedWindowRateLimiter<string>(key, windowOptions), logger)));
                }
            });

        return services;
    }

    // One budget per client per endpoint, e.g. "203.0.113.7|GET api/wallets/walletId:long".
    // The route template (not the raw path) is used so /wallets/1 and /wallets/2 share the same budget.
    private static string GetPartitionKey(HttpContext httpContext)
    {
        var routeTemplate = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? httpContext.Request.Path.Value;

        // The Redis limiter wraps the key in a {hash tag}; nested braces from route parameters would pin
        // every client of an endpoint to the same Redis Cluster slot
        var endpoint = routeTemplate?.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);

        return $"{GetClientIp(httpContext)}|{httpContext.Request.Method} {endpoint}";
    }

    // RemoteIpAddress already holds the real client IP once UseForwardedHeaders has processed trusted proxy headers
    private static string GetClientIp(HttpContext httpContext)
    {
        var address = httpContext.Connection.RemoteIpAddress;

        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(address))
        {
            // A single IPv6 subscriber is usually assigned a whole /64, so limiting individual addresses
            // would let a client bypass the limit simply by rotating through its own range
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return $"{new IPAddress(bytes)}/64";
        }

        return address.ToString();
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, ILogger logger, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;

        if (context.Lease.TryGetMetadata(RateLimitMetadataName.RetryAfter, out var retryAfterSeconds))
        {
            // The window is tracked in whole seconds, so it can report 0 in its last second; "retry now" would only
            // earn another 429, so never advertise less than one second
            httpContext.Response.Headers.RetryAfter = Math.Max(retryAfterSeconds, 1).ToString(CultureInfo.InvariantCulture);
        }

        logger.LogWarning(
            "Rate limit exceeded for {ClientIp} on {Method} {Path}",
            GetClientIp(httpContext),
            httpContext.Request.Method,
            httpContext.Request.Path);

        await httpContext.Response.WriteAsJsonAsync(
            new { error = "Too many requests. Please retry later.", code = "rate_limited" },
            cancellationToken).ConfigureAwait(false);
    }
}
