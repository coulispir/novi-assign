using Core.Service.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Core.Service.Data.Configurations;

public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");

        // The primary key doubles as the uniqueness guard for concurrent requests sharing a key
        builder.HasKey(r => r.Key);

        builder.Property(r => r.Key)
            .HasMaxLength(IdempotencyRecord.MaxKeyLength)
            .IsUnicode(false);

        builder.Property(r => r.RequestHash)
            .IsRequired()
            .HasMaxLength(64)
            .IsFixedLength()
            .IsUnicode(false);

        builder.Property(r => r.Currency)
            .IsRequired()
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false);

        builder.Property(r => r.Balance)
            .IsRequired()
            .HasPrecision(18, 4);

        builder.Property(r => r.CreatedAt)
            .IsRequired();

        builder.HasOne<AccountWallet>()
            .WithMany()
            .HasForeignKey(r => r.WalletId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
