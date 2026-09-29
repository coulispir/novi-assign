using System;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Core.Service.Data;

/// <summary>
/// Creates the context for the <c>dotnet ef</c> tools only; the app itself gets its connection string from configuration.
/// </summary>
public class SystemDbContextFactory : IDesignTimeDbContextFactory<SystemDbContext>
{
    // The same variable the app reads (ConnectionStrings:DefaultConnection), so one setting serves both
    public const string ConnectionStringVariable = "ConnectionStrings__DefaultConnection";

    // No credentials, on purpose. Generating migrations never connects, so this is enough for "migrations add";
    // commands that do connect (e.g. "database update") need the variable above.
    private const string CredentialFreeFallback = "Server=127.0.0.1;Database=FinancialSystemDb;TrustServerCertificate=True;";

    public SystemDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        var optionsBuilder = new DbContextOptionsBuilder<SystemDbContext>();
        optionsBuilder.UseSqlServer(string.IsNullOrWhiteSpace(connectionString) ? CredentialFreeFallback : connectionString, sqlOptions =>
        {
            sqlOptions.MigrationsAssembly("Core.Service");
        });

        return new SystemDbContext(optionsBuilder.Options);
    }
}
