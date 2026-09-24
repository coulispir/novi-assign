using Core.Service.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Core.Service.Data.Configurations;

public class CurrencyValueConfiguration : IEntityTypeConfiguration<CurrencyValue>
{
    public void Configure(EntityTypeBuilder<CurrencyValue> builder)
    {
        // Define explicit table destination
        builder.ToTable("CurrencyValues");

        // Primary Key configuration
        builder.HasKey(c => c.Id);

        // Limit string sizes explicitly to optimize SQL Server page storage
        builder.Property(c => c.CurrencyCode)
            .IsRequired()
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false); // Stores as CHAR(3) instead of NVARCHAR(3) to minimize bytes

        // Enforce high-precision requirements essential for currency rates
        builder.Property(c => c.Rate)
            .IsRequired()
            .HasPrecision(18, 6); // 6 decimal places to accurately process exchange fractions

        builder.Property(c => c.RateDate)
            .IsRequired()
            .HasColumnType("date"); // Maps to native SQL 'date' type omitting time elements

        builder.Property(c => c.UpdatedAt)
            .IsRequired();

        // High-performance Unique Composite Index to enforce historical data integrity
        builder.HasIndex(c => new { c.CurrencyCode, c.RateDate })
            .IsUnique()
            .HasDatabaseName("UX_CurrencyValues_Code_RateDate");
    }
}
