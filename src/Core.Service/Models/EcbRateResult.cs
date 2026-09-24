using System;

namespace Core.Service.Models;

public record EcbRateResult(string CurrencyCode, decimal Rate, DateTime RateDate);
