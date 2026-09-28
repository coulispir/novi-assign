using System;
using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace App.Host.Infrastructure;

public static class ForwardedHeadersServiceCollectionExtensions
{
    private const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// Restores the original client IP and scheme from <c>X-Forwarded-*</c> headers, but only when the request arrives
    /// through a trusted proxy. Trusting everyone would let any client spoof its IP and bypass per-IP rate limiting.
    /// Loopback is trusted by default; load balancers must be listed under <c>ForwardedHeaders:KnownProxies</c>
    /// (single addresses) or <c>ForwardedHeaders:KnownNetworks</c> (CIDR ranges).
    /// </summary>
    public static IServiceCollection AddTrustedForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(SectionName).Get<TrustedProxySettings>() ?? new TrustedProxySettings();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Number of proxy hops to unwind from the right of X-Forwarded-For; must match the real proxy chain
            options.ForwardLimit = settings.ForwardLimit;

            foreach (var proxy in settings.KnownProxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in settings.KnownNetworks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });

        return services;
    }

    private sealed class TrustedProxySettings
    {
        public int ForwardLimit { get; init; } = 1;

        public string[] KnownProxies { get; init; } = Array.Empty<string>();

        public string[] KnownNetworks { get; init; } = Array.Empty<string>();
    }
}
