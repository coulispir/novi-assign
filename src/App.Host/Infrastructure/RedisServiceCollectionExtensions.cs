using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using StackExchange.Redis;

namespace App.Host.Infrastructure;

public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Registers a single, application-wide <see cref="IConnectionMultiplexer"/>. The multiplexer is thread-safe and
    /// designed to be shared, so every Redis consumer (rate limiting, caching, ...) must resolve this instance.
    /// </summary>
    public static IServiceCollection AddRedis(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetRequiredConnectionString("Redis");

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(connectionString);

            // Keep reconnecting in the background instead of crashing the host when Redis is briefly unavailable
            options.AbortOnConnectFail = false;

            // While disconnected, fail commands immediately rather than queueing them until the timeout expires:
            // callers (rate limiting, caching) have a fallback and must not add seconds of latency to every request
            options.BacklogPolicy = BacklogPolicy.FailFast;

            return ConnectionMultiplexer.Connect(options);
        });

        return services;
    }
}
