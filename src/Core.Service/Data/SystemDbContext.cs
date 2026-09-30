using Core.Service.Entities;

using Microsoft.EntityFrameworkCore;

namespace Core.Service.Data;

public class SystemDbContext : DbContext
{
    public SystemDbContext(DbContextOptions<SystemDbContext> options) : base(options)
    {
    }

    public DbSet<AccountWallet> AccountWallets => Set<AccountWallet>();
    public DbSet<CurrencyValue> CurrencyValues => Set<CurrencyValue>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Picks up every IEntityTypeConfiguration in Data/Configurations
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SystemDbContext).Assembly);
    }
}
