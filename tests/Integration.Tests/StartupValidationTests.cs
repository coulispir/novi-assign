using System;
using System.Threading.Tasks;

using App.Host.Infrastructure;

using FluentAssertions;

using Integration.Tests.RateLimiting;

using Microsoft.Extensions.Options;

namespace Integration.Tests;

/// <summary>
/// Misconfiguration must stop the host at startup with a message naming the setting, rather than surface later as a
/// failing request or, for trusted proxies, as silently wrong client IPs.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class StartupValidationTests
{
    private const string Section = ForwardedHeadersServiceCollectionExtensions.SectionName;

    private readonly RedisFixture _redis;

    public StartupValidationTests(RedisFixture redis)
    {
        _redis = redis;
    }

    public static TheoryData<string, string, string> InvalidForwardedHeaders => new()
    {
        { $"{Section}:KnownProxies:0", "10.0.0.300", "KnownProxies" },
        { $"{Section}:KnownNetworks:0", "10.0.0.0/33", "KnownNetworks" },
        { $"{Section}:ForwardLimit", "0", "ForwardLimit" },
    };

    [Theory]
    [MemberData(nameof(InvalidForwardedHeaders))]
    public async Task FailsToStartWithInvalidForwardedHeadersSettings(string key, string value, string setting)
    {
        var start = async () =>
        {
            await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString, settings => settings[key] = value);
        };

        (await start.Should().ThrowAsync<OptionsValidationException>()).WithMessage($"*'{Section}:{setting}'*");
    }

    [Fact]
    public async Task FailsToStartWithoutARedisConnectionString()
    {
        var start = async () =>
        {
            await using var app = await RateLimitedApp.StartAsync(_redis.ConnectionString, settings => settings.Remove("ConnectionStrings:Redis"));
        };

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*'Redis'*ConnectionStrings__Redis*");
    }
}
