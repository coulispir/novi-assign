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

        builder.Property(w => w.Currency)
            .IsRequired()
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false);

        builder.Property(w => w.Balance)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(w => w.CreatedAt)
            .IsRequired();

        builder.Property(w => w.UpdatedAt)
            .IsRequired();

        // SQL Server changes the rowversion on every update, and EF only saves if it still matches what was read.
        // If two requests read the same wallet and both try to save, the second one gets a DbUpdateConcurrencyException
        // instead of overwriting the first.
        builder.Property(w => w.RowVersion)
            .IsRowVersion()
            .IsConcurrencyToken();
    }
}
