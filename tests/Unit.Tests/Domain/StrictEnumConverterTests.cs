using System;
using System.ComponentModel;

using Core.Service.Strategies;

using FluentAssertions;

namespace Unit.Tests.Domain;

public sealed class StrictEnumConverterTests
{
    // Resolved the way MVC's model binding resolves it, so this also proves the [TypeConverter] attribute is picked up
    private readonly TypeConverter _converter = TypeDescriptor.GetConverter(typeof(BalanceStrategyType));

    [Theory]
    [InlineData("AddFundsStrategy", BalanceStrategyType.AddFundsStrategy)]
    [InlineData("subtractfundsstrategy", BalanceStrategyType.SubtractFundsStrategy)]
    [InlineData(" FORCESUBTRACTFUNDSSTRATEGY ", BalanceStrategyType.ForceSubtractFundsStrategy)]
    public void ConvertFrom_WithAMemberName_IgnoresCase(string text, BalanceStrategyType expected)
    {
        _converter.ConvertFrom(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("42")]
    [InlineData("")]
    [InlineData("TransferStrategy")]
    [InlineData("AddFundsStrategy,SubtractFundsStrategy")]
    public void ConvertFrom_WithAnythingButAMemberName_Throws(string text)
    {
        var convert = () => _converter.ConvertFrom(text);

        convert.Should().Throw<FormatException>().WithMessage("*Allowed values: AddFundsStrategy, SubtractFundsStrategy, ForceSubtractFundsStrategy*");
    }
}
