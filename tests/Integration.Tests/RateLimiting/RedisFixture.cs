using System.Threading.Tasks;

using Testcontainers.Redis;

namespace Integration.Tests.RateLimiting;

/// <summary>
/// Starts one disposable Redis container shared by every test in the <see cref="RedisCollection"/>.
/// Tests stay isolated by using a unique client IP each, so they never touch each other's counters.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:8-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
