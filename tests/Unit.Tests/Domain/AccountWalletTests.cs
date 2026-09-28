using System;

using Core.Service.Entities;

using FluentAssertions;

namespace Unit.Tests.Domain;

public sealed class AccountWalletTests
{
    [Fact]
    public void Create_WithValidData_NormalizesTheCurrencyAndSetsTheBalance()
    {
        var wallet = AccountWallet.Create("usd", 25.5m);

        wallet.Currency.Should().Be("USD");
        wallet.Balance.Should().Be(25.5m);
    }

    [Fact]
    public void Create_WithoutInitialBalance_StartsAtZero()
    {
        AccountWallet.Create("EUR").Balance.Should().Be(0m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("EU")]
    [InlineData("EURO")]
    public void Create_WithInvalidCurrency_Throws(string currency)
    {
        var create = () => AccountWallet.Create(currency, 10m);

        create.Should().Throw<ArgumentException>().WithParameterName("currency");
    }

    [Fact]
    public void Create_WithNegativeInitialBalance_Throws()
    {
        var create = () => AccountWallet.Create("EUR", -0.01m);

        create.Should().Throw<ArgumentException>().WithParameterName("initialBalance");
    }

    [Fact]
    public void Credit_AddsTheAmount()
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        wallet.Credit(2.5m);

        wallet.Balance.Should().Be(12.5m);
    }

    [Fact]
    public void Debit_SubtractsTheAmount()
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        wallet.Debit(10m);

        wallet.Balance.Should().Be(0m);
    }

    [Fact]
    public void Debit_BeyondTheBalance_ThrowsAndLeavesTheBalanceUnchanged()
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        var debit = () => wallet.Debit(10.01m);

        debit.Should().Throw<InvalidOperationException>();
        wallet.Balance.Should().Be(10m);
    }

    [Fact]
    public void ForceDebit_BeyondTheBalance_AllowsANegativeBalance()
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        wallet.ForceDebit(15m);

        wallet.Balance.Should().Be(-5m);
    }

    public static TheoryData<Action<AccountWallet>> NonPositiveAmountOperations => new()
    {
        wallet => wallet.Credit(0m),
        wallet => wallet.Credit(-1m),
        wallet => wallet.Debit(0m),
        wallet => wallet.Debit(-1m),
        wallet => wallet.ForceDebit(0m),
        wallet => wallet.ForceDebit(-1m),
    };

    [Theory]
    [MemberData(nameof(NonPositiveAmountOperations))]
    public void BalanceOperations_WithNonPositiveAmount_ThrowAndLeaveTheBalanceUnchanged(Action<AccountWallet> operation)
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        var apply = () => operation(wallet);

        apply.Should().Throw<ArgumentException>();
        wallet.Balance.Should().Be(10m);
    }
}
