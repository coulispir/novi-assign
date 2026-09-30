using System;

namespace Core.Service.Entities;

public class CurrencyValue
{
    public long Id { get; private set; }

    // 3-letter ISO code, stored upper case (e.g. "USD")
    public string CurrencyCode { get; private set; } = string.Empty;

    // How much one euro buys in this currency
    public decimal Rate { get; private set; }

    // The ECB publication date. Date only, no time
    public DateTime RateDate { get; private set; }

    // When this row was last written (UTC)
    public DateTime UpdatedAt { get; private set; }

    // For EF Core
    private CurrencyValue() { }

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
            RateDate = rateDate.Date, // There's one rate per currency per day, so drop the time
            UpdatedAt = DateTime.UtcNow
        };
    }

    public void UpdateRate(decimal newRate, DateTime rateDate)
    {
        if (newRate <= 0)
            throw new ArgumentException("Exchange rate must be greater than zero.", nameof(newRate));

        Rate = newRate;
        RateDate = rateDate.Date;
        UpdatedAt = DateTime.UtcNow;
    }
}
