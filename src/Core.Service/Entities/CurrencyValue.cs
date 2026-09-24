using System;

namespace Core.Service.Entities;

public class CurrencyValue
{
    // EF Core Shadow Key / Primary Key
    public long Id { get; private set; }

    // ISO Currency Code (e.g., "USD", "GBP", "EUR")
    public string CurrencyCode { get; private set; } = string.Empty;

    // Exchange rate relative to base currency (EUR)
    public decimal Rate { get; private set; }

    // Timestamp when this rate was officially recorded by the ECB
    public DateTime RateDate { get; private set; }

    // System Audit Timestamp
    public DateTime UpdatedAt { get; private set; }

    // Empty constructor required by EF Core materialization
    private CurrencyValue() { }

    // Domain Factory Method to safely initialize a currency entry
    public static CurrencyValue Create(string currencyCode, decimal rate, DateTime rateDate)
    {
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("Currency code must be a valid 3-letter ISO string.", nameof(currencyCode));

        if (rate <= 0)
            throw new ArgumentException("Exchange rate must be greater than zero.", nameof(rate));

        return new CurrencyValue
        {
            CurrencyCode = currencyCode.ToUpperInvariant(),
            Rate = rate,
            RateDate = rateDate.Date, // Keep date-only accuracy
            UpdatedAt = DateTime.UtcNow
        };
    }

    // Explicit domain behavior to update an existing rate cleanly
    public void UpdateRate(decimal newRate, DateTime rateDate)
    {
        if (newRate <= 0)
            throw new ArgumentException("Exchange rate must be greater than zero.", nameof(newRate));

        Rate = newRate;
        RateDate = rateDate.Date;
        UpdatedAt = DateTime.UtcNow;
    }
}
