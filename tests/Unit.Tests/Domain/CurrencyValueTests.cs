using System;

using Core.Service.Entities;

using FluentAssertions;

namespace Unit.Tests.Domain;

public sealed class CurrencyValueTests
{
    private static readonly DateTime RateDate = new(2026, 9, 25, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_WithValidData_NormalizesTheCodeAndKeepsOnlyTheDate()
    {
        var value = CurrencyValue.Create("usd", 1.1472m, RateDate);

        value.CurrencyCode.Should().Be("USD");
        value.Rate.Should().Be(1.1472m);
        value.RateDate.Should().Be(RateDate.Date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDT")]
    public void Create_WithInvalidCurrencyCode_Throws(string currencyCode)
    {
        var create = () => CurrencyValue.Create(currencyCode, 1.1m, RateDate);

        create.Should().Throw<ArgumentException>().WithParameterName("currencyCode");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    public void Create_WithNonPositiveRate_Throws(decimal rate)
    {
        var create = () => CurrencyValue.Create("USD", rate, RateDate);

        create.Should().Throw<ArgumentException>().WithParameterName("rate");
    }

    [Fact]
    public void UpdateRate_ReplacesTheRateAndDate()
    {
        var value = CurrencyValue.Create("USD", 1.10m, RateDate);
        var nextDay = RateDate.AddDays(1);

        value.UpdateRate(1.12m, nextDay);

        value.Rate.Should().Be(1.12m);
        value.RateDate.Should().Be(nextDay.Date);
    }

    [Fact]
    public void UpdateRate_WithNonPositiveRate_ThrowsAndKeepsTheCurrentRate()
    {
        var value = CurrencyValue.Create("USD", 1.10m, RateDate);

        var update = () => value.UpdateRate(0m, RateDate);

        update.Should().Throw<ArgumentException>().WithParameterName("newRate");
        value.Rate.Should().Be(1.10m);
    }
}
