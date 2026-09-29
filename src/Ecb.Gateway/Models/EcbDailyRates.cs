using System;
using System.Collections.Generic;

namespace Ecb.Gateway.Models;

/// <summary>
/// One publication of the ECB euro foreign exchange reference rates: every rate is the price of one euro in
/// <see cref="EcbRate.Currency"/>, so EUR itself is not listed.
/// </summary>
public record EcbDailyRates(DateOnly Date, IReadOnlyList<EcbRate> Rates)
{
    public const string BaseCurrency = "EUR";
}

public record EcbRate(string Currency, decimal Rate);
