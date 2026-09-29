using System;
using System.Collections.Generic;

using Core.Service.Exceptions;
using Core.Service.Services;

using FluentAssertions;

namespace Unit.Tests.Application;

public sealed class CurrencyConverterTests
{
    // Rates against EUR, as published by the ECB
    private static readonly IReadOnlyDictionary<string, decimal> Rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1.1403m,
        ["GBP"] = 0.86045m,
    };

    [Theory]
    [InlineData(100, "USD", "GBP", 75.4582)] // 100 / 1.1403 * 0.86045, rounded to 4 decimals
    [InlineData(100, "EUR", "USD", 114.03)]
    [InlineData(114.03, "USD", "EUR", 100)]
    [InlineData(50, "usd", "eur", 43.8481)] // 50 / 1.1403
    public void Convert_ConvertsThroughEuroAndRoundsToFourDecimals(decimal amount, string from, string to, decimal expected)
    {
        CurrencyConverter.Convert(amount, from, to, Rates).Should().Be(expected);
    }

    [Fact]
    public void Convert_BetweenEuroAndEuro_NeedsNoRates()
    {
        CurrencyConverter.Convert(12.5m, "EUR", "EUR", new Dictionary<string, decimal>()).Should().Be(12.5m);
    }

    [Theory]
    [InlineData("XYZ", "EUR")]
    [InlineData("EUR", "XYZ")]
    public void Convert_WithUnknownCurrency_ThrowsUnsupportedCurrency(string from, string to)
    {
        var convert = () => CurrencyConverter.Convert(10m, from, to, Rates);

        convert.Should().Throw<UnsupportedCurrencyException>().WithMessage($"*{from} to {to}*");
    }

    [Fact]
    public void Convert_WithAmountBelowTheSmallestUnit_RoundsToZero()
    {
        // The service rejects this rather than applying a zero adjustment
        CurrencyConverter.Convert(0.00001m, "USD", "EUR", Rates).Should().Be(0m);
    }
}
