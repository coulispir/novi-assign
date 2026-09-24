using Core.Service.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Core.Service.Data.Configurations;

public class AccountWalletConfiguration : IEntityTypeConfiguration<AccountWallet>
{
    public void Configure(EntityTypeBuilder<AccountWallet> builder)
    {
        builder.ToTable("AccountWallets");

        builder.HasKey(w => w.Id);

        builder.Property(w => w.AccountId)
            .IsRequired()
            .HasMaxLength(50)
            .IsUnicode(false); // Indexed system identifiers stay optimal as VARCHAR

        builder.Property(w => w.Currency)
            .IsRequired()
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false);

        // Standard financial precision requirements
        builder.Property(w => w.Balance)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(w => w.CreatedAt)
            .IsRequired();

        builder.Property(w => w.UpdatedAt)
            .IsRequired();

        // 🛡️ OPTIMISTIC CONCURRENCY PROTECTION
        // Maps to a native SQL Server 'rowversion' / 'timestamp' data column.
        // If two threads read a wallet and try to update its balance at the exact same instant, 
        // SQL Server will reject the second transaction with a DbUpdateConcurrencyException.
        builder.Property(w => w.RowVersion)
            .IsRowVersion()
            .IsConcurrencyToken();

        // Unique rule: An individual customer account can have only ONE wallet per specific currency
        builder.HasIndex(w => new { w.AccountId, w.Currency })
            .IsUnique()
            .HasDatabaseName("UX_AccountWallets_AccountId_Currency");
    }
}
