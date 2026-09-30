using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using App.Host.Infrastructure;
using App.Host.Infrastructure.RateLimiting;

using Core.Service.Handlers;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Wallet.Api;

namespace Integration.Tests.RateLimiting;

/// <summary>
/// In-memory host running the real <c>WalletController</c> with the production rate limiting registrations and the
/// same middleware order as <c>Program.cs</c>. Only the wallet handler is substituted, so no database is needed.
/// </summary>
internal sealed class RateLimitedApp : IAsyncDisposable
{
    public const int ReadLimit = 5;
    public const int CreateLimit = 2;
    public const int AdjustLimit = 3;
    public const string TrustedProxy = "10.0.0.1";

    // TestServer has no TCP connection, so tests declare the peer address the server should see through this header
    private const string PeerAddressHeader = "X-Test-Peer-Address";

    private static int s_clientCounter;

    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private RateLimitedApp(WebApplication app)
    {
        _app = app;
        _client = app.GetTestClient();
    }

    /// <summary>
    /// Returns an IP no other test has used, so every test starts with a fresh budget in the shared Redis.
    /// </summary>
    public static string NextClientIp()
    {
        var n = Interlocked.Increment(ref s_clientCounter);
        return $"198.18.{(n >> 8) & 255}.{n & 255}";
    }

    public static async Task<RateLimitedApp> StartAsync(string redisConnectionString, Action<IDictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = redisConnectionString,
            [$"{RateLimitPolicyOptions.SectionName}:{RateLimitPolicies.WalletRead}:PermitLimit"] = $"{ReadLimit}",
            [$"{RateLimitPolicyOptions.SectionName}:{RateLimitPolicies.WalletRead}:Window"] = "00:01:00",
            [$"{RateLimitPolicyOptions.SectionName}:{RateLimitPolicies.WalletCreate}:PermitLimit"] = $"{CreateLimit}",
            [$"{RateLimitPolicyOptions.SectionName}:{RateLimitPolicies.WalletCreate}:Window"] = "00:01:00",
            [$"{RateLimitPolicyOptions.SectionName}:{RateLimitPolicies.WalletAdjust}:PermitLimit"] = $"{AdjustLimit}",
            [$"{RateLimitPolicyOptions.SectionName}:{RateLimitPolicies.WalletAdjust}:Window"] = "00:01:00",
            ["ForwardedHeaders:KnownProxies:0"] = TrustedProxy,
        };
        configure?.Invoke(settings);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddRedis(builder.Configuration);
        builder.Services.AddTrustedForwardedHeaders(builder.Configuration);
        builder.Services.AddClientIpRateLimiting(builder.Configuration, RateLimitPolicies.All);
        AddWalletHandlers(builder.Services);
        builder.Services.AddWalletApi();

        var app = builder.Build();

        app.Use(SimulatePeerAddress);
        app.UseForwardedHeaders();
        app.UseRouting();
        app.UseRateLimiter();
        app.MapControllers();

        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return new RateLimitedApp(app);
    }

    public Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, string peerAddress, string? forwardedFor = null)
    {
        ArgumentNullException.ThrowIfNull(createRequest);

        var request = createRequest();
        request.Headers.Add(PeerAddressHeader, peerAddress);

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return _client.SendAsync(request);
    }

    /// <summary>
    /// Sends <paramref name="attempts"/> sequential requests and returns how many were not rejected with 429.
    /// </summary>
    public async Task<int> CountAllowedAsync(Func<HttpRequestMessage> createRequest, string peerAddress, int attempts, string? forwardedFor = null)
    {
        var allowed = 0;

        for (var i = 0; i < attempts; i++)
        {
            using var response = await SendAsync(createRequest, peerAddress, forwardedFor);

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                allowed++;
            }
        }

        return allowed;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private static Task SimulatePeerAddress(HttpContext context, Func<Task> next)
    {
        if (context.Request.Headers.TryGetValue(PeerAddressHeader, out var peerAddress))
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peerAddress.ToString());
        }

        return next();
    }

    private static void AddWalletHandlers(IServiceCollection services)
    {
        var getBalance = Substitute.For<IGetBalanceHandler>();
        getBalance.HandleAsync(default!, default)
            .ReturnsForAnyArgs(new BalanceResult(1, 10m, "EUR", 10m, "EUR"));

        var createWallet = Substitute.For<ICreateWalletHandler>();
        createWallet.HandleAsync(default!, default)
            .ReturnsForAnyArgs(new WalletResult(1, "EUR", 10m));

        var adjustBalance = Substitute.For<IAdjustBalanceHandler>();
        adjustBalance.HandleAsync(default!, default)
            .ReturnsForAnyArgs(new WalletAdjustmentResult(1, "EUR", 20m, IsReplay: false));

        services.AddSingleton(getBalance);
        services.AddSingleton(createWallet);
        services.AddSingleton(adjustBalance);
    }
}
