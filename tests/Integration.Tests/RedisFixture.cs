using System.Threading.Tasks;

using Testcontainers.Redis;

namespace Integration.Tests;

/// <summary>
/// Starts one disposable Redis container shared by every test in the <see cref="RedisCollection"/>.
/// Tests in a collection run one at a time; rate limiting tests stay isolated by using a unique client IP each, and
/// cache tests reset the key they use before each test.
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
