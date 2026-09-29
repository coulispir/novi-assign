using System;

using Core.Service.Data;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

namespace Unit.Tests.Application;

/// <summary>
/// The design-time factory used by the dotnet ef tools. Both tests change the same process-wide variable, so they live
/// in one class, whose tests xUnit runs one at a time.
/// </summary>
public sealed class SystemDbContextFactoryTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable(SystemDbContextFactory.ConnectionStringVariable);

    public void Dispose() => Environment.SetEnvironmentVariable(SystemDbContextFactory.ConnectionStringVariable, _original);

    [Fact]
    public void CreateDbContext_WithTheVariableSet_UsesItsConnectionString()
    {
        Environment.SetEnvironmentVariable(SystemDbContextFactory.ConnectionStringVariable, "Server=db.example;Database=Wallets;Integrated Security=True;");

        using var context = new SystemDbContextFactory().CreateDbContext([]);

        context.Database.GetConnectionString().Should().Contain("db.example");
    }

    [Fact]
    public void CreateDbContext_WithoutTheVariable_FallsBackToAConnectionStringWithoutCredentials()
    {
        Environment.SetEnvironmentVariable(SystemDbContextFactory.ConnectionStringVariable, null);

        using var context = new SystemDbContextFactory().CreateDbContext([]);

        var connectionString = context.Database.GetConnectionString();
        connectionString.Should().NotContainEquivalentOf("Password").And.NotContainEquivalentOf("User Id");
    }
}
