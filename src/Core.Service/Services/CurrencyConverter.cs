using System;
using System.Collections.Generic;

using Core.Service.Exceptions;

namespace Core.Service.Services;

/// <summary>
/// Converts amounts between currencies through EUR, using the latest ECB rates (each quoted against EUR).
/// Shared by balance conversion and cross-currency adjustments, so both always apply the same maths.
/// </summary>
public static class CurrencyConverter
{
    // Balances are stored as decimal(18,4)
    public const int Decimals = 4;

    public const string BaseCurrency = "EUR";

    /// <summary>
    /// Returns <paramref name="amount"/> in <paramref name="toCurrency"/>, rounded to <see cref="Decimals"/> places.
    /// Throws <see cref="UnsupportedCurrencyException"/> when either currency has no known rate.
    /// </summary>
    public static decimal Convert(decimal amount, string fromCurrency, string toCurrency, IReadOnlyDictionary<string, decimal> euroRates)
    {
        ArgumentNullException.ThrowIfNull(fromCurrency);
        ArgumentNullException.ThrowIfNull(toCurrency);
        ArgumentNullException.ThrowIfNull(euroRates);

        var fromRate = GetEuroRate(euroRates, fromCurrency);
        var toRate = GetEuroRate(euroRates, toCurrency);

        if (fromRate is null || toRate is null || fromRate == 0)
            throw new UnsupportedCurrencyException($"No exchange rate is available for converting {fromCurrency.ToUpperInvariant()} to {toCurrency.ToUpperInvariant()}.");

        return Math.Round(amount / fromRate.Value * toRate.Value, Decimals);
    }

    private static decimal? GetEuroRate(IReadOnlyDictionary<string, decimal> euroRates, string currency)
    {
        if (string.Equals(currency, BaseCurrency, StringComparison.OrdinalIgnoreCase)) return 1.0m;

        return euroRates.TryGetValue(currency, out var rate) ? rate : null;
    }
}
