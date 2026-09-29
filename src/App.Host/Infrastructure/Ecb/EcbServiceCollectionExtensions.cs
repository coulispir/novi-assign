using System;

using Core.Service.Decorators;
using Core.Service.Interfaces;

using Ecb.Gateway;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace App.Host.Infrastructure.Ecb;

public static class EcbServiceCollectionExtensions
{
    public const string SectionName = "Ecb";

    /// <summary>
    /// Registers the standalone <see cref="IEcbClient"/> with its feed URL and timeout from <c>Ecb</c>, and the core's
    /// <see cref="IEcbGateway"/> port on top of it: the adapter, wrapped in the logging decorator. Invalid settings fail
    /// at startup.
    /// </summary>
    public static IServiceCollection AddEcbGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EcbClientOptions>()
            .Bind(configuration.GetSection(SectionName))
            .Validate(
                options => options.DailyRatesUrl is { IsAbsoluteUri: true } url && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp),
                $"'{SectionName}:DailyRatesUrl' must be an absolute http(s) URL.")
            .Validate(
                options => options.Timeout > TimeSpan.Zero,
                $"'{SectionName}:Timeout' must be greater than 00:00:00.")
            .ValidateOnStart();

        services.AddHttpClient<IEcbClient, EcbClient>((serviceProvider, client) =>
        {
            client.Timeout = serviceProvider.GetRequiredService<IOptions<EcbClientOptions>>().Value.Timeout;
            client.DefaultRequestHeaders.Add("User-Agent", "WalletManagementSystem/1.0");
        });

        // Decorator pattern with the built-in container: the adapter is registered as itself, and IEcbGateway resolves
        // to the logging decorator wrapping it. Callers only ever see IEcbGateway.
        services.AddScoped<EcbGatewayAdapter>();
        services.AddScoped<IEcbGateway>(serviceProvider => new LoggingEcbGatewayDecorator(
            serviceProvider.GetRequiredService<EcbGatewayAdapter>(),
            serviceProvider.GetRequiredService<ILogger<LoggingEcbGatewayDecorator>>()));

        return services;
    }
}
