using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Core.Service.Data;

public class SystemDbContextFactory : IDesignTimeDbContextFactory<SystemDbContext>
{
    public SystemDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SystemDbContext>();
        
        // Local fallback string used strictly for design-time file generation tasks.
        // This does not affect your production runtime docker environment values.
        const string localFallbackConnectionString = "Server=127.0.0.1;Database=FinancialSystemDb;User Id=sa;Password=YourSecure@Password123;TrustServerCertificate=True;";

        optionsBuilder.UseSqlServer(localFallbackConnectionString, sqlOptions =>
        {
            sqlOptions.MigrationsAssembly("Core.Service");
        });

        return new SystemDbContext(optionsBuilder.Options);
    }
}
