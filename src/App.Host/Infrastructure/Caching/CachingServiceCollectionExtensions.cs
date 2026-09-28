using Core.Service.Interfaces;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace App.Host.Infrastructure.Caching;

public static class CachingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Redis-backed <see cref="ICurrencyRatesCache"/>. Requires <see cref="RedisServiceCollectionExtensions.AddRedis"/>,
    /// whose shared connection it reuses.
    /// </summary>
    public static IServiceCollection AddCurrencyRatesCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CurrencyRatesCacheOptions>()
            .Bind(configuration.GetSection(CurrencyRatesCacheOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                $"The currency rates cache must be configured under '{CurrencyRatesCacheOptions.SectionName}' with TimeToLive > 00:00:00.")
            .ValidateOnStart();

        // Stateless apart from the thread-safe multiplexer, so one instance serves every request
        services.AddSingleton<ICurrencyRatesCache, RedisCurrencyRatesCache>();

        return services;
    }
}
