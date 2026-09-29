using System;

using Core.Service.Entities;
using Core.Service.Exceptions;

using FluentAssertions;

namespace Unit.Tests.Entities;

public sealed class AccountWalletTests
{
    [Theory]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("EURO")]
    public void RejectsAnInvalidCurrencyAsAValidationError(string currency)
    {
        var create = () => AccountWallet.Create(currency, 10m);

        create.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void RejectsANegativeInitialBalanceAsAValidationError()
    {
        var create = () => AccountWallet.Create("EUR", -1m);

        create.Should().Throw<DomainValidationException>();
    }

    [Fact]
    public void DebitBeyondTheBalanceThrowsInsufficientFundsAndLeavesTheBalanceUnchanged()
    {
        var wallet = AccountWallet.Create("EUR", 10m);

        var debit = () => wallet.Debit(10.01m);

        debit.Should().Throw<InsufficientFundsException>();
        wallet.Balance.Should().Be(10m);
    }

    [Fact]
    public void GuardClausesStillThrowArgumentExceptionForProgrammingErrors()
    {
        // Amounts are validated by the handler first, so a non-positive amount here is a bug, not a client error
        var wallet = AccountWallet.Create("EUR", 10m);

        var debit = () => wallet.Debit(0m);

        debit.Should().Throw<ArgumentException>();
    }
}
