using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using FluentAssertions;

using Functional.Tests.Infrastructure;

namespace Functional.Tests;

/// <summary>
/// The real app, with real SQL Server and Redis, reports itself healthy on both health endpoints.
/// </summary>
[Collection(WalletApiCollection.Name)]
public sealed class HealthCheckTests
{
    private readonly HttpClient _client;

    public HealthCheckTests(WalletApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ReadinessIsHealthyWhenSqlServerAndRedisAreUp()
    {
        using var response = await _client.GetAsync(new Uri("/health", UriKind.Relative));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        body.RootElement.GetProperty("checks").GetProperty("sql-server").GetProperty("status").GetString().Should().Be("Healthy");
        body.RootElement.GetProperty("checks").GetProperty("redis").GetProperty("status").GetString().Should().Be("Healthy");
    }

    [Fact]
    public async Task LivenessIsHealthy()
    {
        using var response = await _client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthResponsesAreNeverCached()
    {
        using var response = await _client.GetAsync(new Uri("/health", UriKind.Relative));

        response.Headers.CacheControl!.NoCache.Should().BeTrue();
    }
}
