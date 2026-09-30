using Core.Service.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Core.Service.Data.Configurations;

public class CurrencyValueConfiguration : IEntityTypeConfiguration<CurrencyValue>
{
    public void Configure(EntityTypeBuilder<CurrencyValue> builder)
    {
        builder.ToTable("CurrencyValues");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.CurrencyCode)
            .IsRequired()
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false); // char(3): currency codes are always 3 ASCII letters

        builder.Property(c => c.Rate)
            .IsRequired()
            .HasPrecision(18, 6); // More decimal places than the ECB publishes (e.g. 0.86045), so nothing is rounded

        builder.Property(c => c.RateDate)
            .IsRequired()
            .HasColumnType("date");

        builder.Property(c => c.UpdatedAt)
            .IsRequired();

        // One rate per currency per day. The MERGE matches on this, and the "latest rate" query reads it
        builder.HasIndex(c => new { c.CurrencyCode, c.RateDate })
            .IsUnique()
            .HasDatabaseName("UX_CurrencyValues_Code_RateDate");
    }
}
