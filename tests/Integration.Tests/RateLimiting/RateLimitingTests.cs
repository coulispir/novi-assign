using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.Extensions.Options;

using Wallet.Api;

namespace Integration.Tests.RateLimiting;

[Collection(RedisCollection.Name)]
public sealed class RateLimitingTests
{
    private readonly RedisFixture _redis;

    public RateLimitingTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [Fact]
    public async Task RejectsRequestsBeyondTheLimitWith429AndRetryAfter()
    {
        await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString);
        var client = RateLimitedApp.NextClientIp();

        for (var i = 0; i < RateLimitedApp.ReadLimit; i++)
        {
            using var allowed = await app.SendAsync(() => WalletRequests.GetBalance(), client);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var rejected = await app.SendAsync(() => WalletRequests.GetBalance(), client);

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("Too many requests");
    }

    public static TheoryData<string, int> EndpointLimits => new()
    {
        { RateLimitPolicies.WalletRead, RateLimitedApp.ReadLimit },
        { RateLimitPolicies.WalletCreate, RateLimitedApp.CreateLimit },
        { RateLimitPolicies.WalletAdjust, RateLimitedApp.AdjustLimit },
    };

    [Theory]
    [MemberData(nameof(EndpointLimits))]
    public async Task EachEndpointEnforcesItsOwnConfiguredLimit(string policy, int expectedLimit)
    {
        await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString);

        var allowed = await app.CountAllowedAsync(RequestFor(policy), RateLimitedApp.NextClientIp(), attempts: expectedLimit + 3);

        allowed.Should().Be(expectedLimit);
    }

    [Fact]
    public async Task ExhaustingOneEndpointDoesNotAffectAnother()
    {
        await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString);
        var client = RateLimitedApp.NextClientIp();

        await app.CountAllowedAsync(WalletRequests.AdjustBalance, client, attempts: RateLimitedApp.AdjustLimit + 1);

        using var response = await app.SendAsync(() => WalletRequests.GetBalance(), client);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DifferentRouteValuesShareTheEndpointBudget()
    {
        await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString);
        var client = RateLimitedApp.NextClientIp();
        var walletId = 0;

        // A client must not escape the limit by varying the wallet id in the URL
        var allowed = await app.CountAllowedAsync(() => WalletRequests.GetBalance(++walletId), client, attempts: RateLimitedApp.ReadLimit + 3);

        allowed.Should().Be(RateLimitedApp.ReadLimit);
    }

    [Fact]
    public async Task ClientsAreLimitedIndependently()
    {
        await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString);

        await app.CountAllowedAsync(() => WalletRequests.GetBalance(), RateLimitedApp.NextClientIp(), attempts: RateLimitedApp.ReadLimit + 1);

        using var response = await app.SendAsync(() => WalletRequests.GetBalance(), RateLimitedApp.NextClientIp());
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConcurrentRequestsNeverExceedTheLimit()
    {
        await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString);
        var client = RateLimitedApp.NextClientIp();

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => app.SendAsync(() => WalletRequests.GetBalance(), client)));

        try
        {
            responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(RateLimitedApp.ReadLimit);
            responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests).Should().Be(50 - RateLimitedApp.ReadLimit);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task LimitIsSharedAcrossApplicationNodes()
    {
        await using var node1 = await RateLimitedApp.StartAsync(_redis.ConnectionString);
        await using var node2 = await RateLimitedApp.StartAsync(_redis.ConnectionString);
        var client = RateLimitedApp.NextClientIp();

        // A load balancer spreading one client's requests across nodes must not multiply its allowance
        var allowed = 0;
        for (var i = 0; i < RateLimitedApp.ReadLimit * 2; i++)
        {
            var node = i % 2 == 0 ? node1 : node2;
            allowed += await node.CountAllowedAsync(() => WalletRequests.GetBalance(), client, attempts: 1);
        }

        allowed.Should().Be(RateLimitedApp.ReadLimit);
    }

    [Fact]
    public async Task UsesForwardedClientIpWhenRequestComesFromTrustedProxy()
    {
        await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString);
        var exhaustedClient = RateLimitedApp.NextClientIp();

        await app.CountAllowedAsync(() => WalletRequests.GetBalance(), RateLimitedApp.TrustedProxy, RateLimitedApp.ReadLimit + 1, forwardedFor: exhaustedClient);

        // Another client behind the same load balancer still has its own budget
        using var otherClient = await app.SendAsync(() => WalletRequests.GetBalance(), RateLimitedApp.TrustedProxy, forwardedFor: RateLimitedApp.NextClientIp());
        otherClient.StatusCode.Should().Be(HttpStatusCode.OK);

        using var sameClient = await app.SendAsync(() => WalletRequests.GetBalance(), RateLimitedApp.TrustedProxy, forwardedFor: exhaustedClient);
        sameClient.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task IgnoresForwardedHeaderFromUntrustedPeer()
    {
        await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString);
        var attacker = RateLimitedApp.NextClientIp();

        // Spoofing a different X-Forwarded-For on every request must not reset the attacker's budget
        var allowed = 0;
        for (var i = 0; i < RateLimitedApp.ReadLimit + 3; i++)
        {
            allowed += await app.CountAllowedAsync(() => WalletRequests.GetBalance(), attacker, attempts: 1, forwardedFor: RateLimitedApp.NextClientIp());
        }

        allowed.Should().Be(RateLimitedApp.ReadLimit);
    }

    [Fact]
    public async Task Ipv6AddressesInTheSame64ShareABudget()
    {
        await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString);
        var prefix = $"2001:db8:{Random.Shared.Next(0x10000):x}:{Random.Shared.Next(0x10000):x}";

        // Rotating the interface identifier inside one /64 must not reset the client's budget
        var allowed = 0;
        for (var i = 1; i <= RateLimitedApp.ReadLimit + 3; i++)
        {
            allowed += await app.CountAllowedAsync(() => WalletRequests.GetBalance(), $"{prefix}::{i:x}", attempts: 1);
        }

        allowed.Should().Be(RateLimitedApp.ReadLimit);
    }

    [Fact]
    public async Task AllowsRequestsWhenRedisIsUnavailable()
    {
        // Nothing listens on port 1, so every Redis call fails immediately
        await using var app = await RateLimitedApp.StartAsync("127.0.0.1:1,connectTimeout=500");

        var allowed = await app.CountAllowedAsync(() => WalletRequests.GetBalance(), RateLimitedApp.NextClientIp(), attempts: RateLimitedApp.ReadLimit + 3);

        allowed.Should().Be(RateLimitedApp.ReadLimit + 3);
    }

    [Fact]
    public async Task FailsToStartWhenAPolicyIsNotConfigured()
    {
        var startWithoutAdjustPolicy = async () =>
        {
            await using var app = await RateLimitedApp.StartAsync(
                _redis.ConnectionString,
                settings =>
                {
                    foreach (var key in settings.Keys.Where(k => k.Contains(RateLimitPolicies.WalletAdjust, StringComparison.Ordinal)).ToList())
                    {
                        settings.Remove(key);
                    }
                });
        };

        (await startWithoutAdjustPolicy.Should().ThrowAsync<OptionsValidationException>())
            .WithMessage($"*'{RateLimitPolicies.WalletAdjust}'*");
    }

    private static Func<HttpRequestMessage> RequestFor(string policy) => policy switch
    {
        RateLimitPolicies.WalletRead => () => WalletRequests.GetBalance(),
        RateLimitPolicies.WalletCreate => WalletRequests.CreateWallet,
        RateLimitPolicies.WalletAdjust => WalletRequests.AdjustBalance,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown rate limit policy."),
    };
}
