using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;

using FluentAssertions;

using Functional.Tests.Infrastructure;

namespace Functional.Tests;

[Collection(WalletApiCollection.Name)]
public sealed class WalletLifecycleTests
{
    private readonly WalletApiClient _api;

    public WalletLifecycleTests(WalletApiFactory factory)
    {
        _api = new WalletApiClient(factory.CreateClient());
    }

    [Fact]
    public async Task CreatesAWalletAndReadsItBack()
    {
        var created = await _api.CreateWalletAsync("eur", 100m);

        created.Id.Should().BePositive();
        created.Currency.Should().Be("EUR");
        created.Balance.Should().Be(100m);

        var balance = await _api.GetBalanceAsync(created.Id);
        balance.Should().Be(new BalanceDto(created.Id, 100m, "EUR", 100m, "EUR"));
    }

    [Theory]
    [InlineData("EURO", 10)]
    [InlineData("EUR", -1)]
    public async Task RejectsAnInvalidWallet(string currency, decimal initialBalance)
    {
        using var response = await _api.CreateAsync(currency, initialBalance);

        await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task ReturnsNotFoundForAnUnknownWallet()
    {
        using var response = await _api.GetAsync(long.MaxValue);

        await response.ShouldBeErrorAsync(HttpStatusCode.NotFound, "wallet_not_found");
    }

    [Fact]
    public async Task AppliesEachStrategyAndPersistsTheBalance()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 100m);

        await AssertAdjustedAsync(wallet.Id, 50m, "AddFundsStrategy", expectedBalance: 150m);
        await AssertAdjustedAsync(wallet.Id, 30m, "SubtractFundsStrategy", expectedBalance: 120m);
        await AssertAdjustedAsync(wallet.Id, 200m, "ForceSubtractFundsStrategy", expectedBalance: -80m);

        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(-80m);
    }

    [Fact]
    public async Task RejectsASubtractionBeyondTheBalanceWithoutChangingIt()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 20m);

        using var response = await _api.AdjustAsync(wallet.Id, 20.01m, "EUR", "SubtractFundsStrategy");

        await response.ShouldBeErrorAsync(HttpStatusCode.UnprocessableEntity, "insufficient_funds");
        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(20m);
    }

    public static TheoryData<decimal, string, string, string?> InvalidAdjustments => new()
    {
        { 10m, "EUR", "TransferStrategy", WalletApiClient.NewIdempotencyKey() },   // unknown strategy
        { 0m, "EUR", "AddFundsStrategy", WalletApiClient.NewIdempotencyKey() },    // non-positive amount
    };

    [Theory]
    [MemberData(nameof(InvalidAdjustments))]
    public async Task RejectsAnInvalidAdjustmentWithoutChangingTheBalance(decimal amount, string currency, string strategy, string? idempotencyKey)
    {
        var wallet = await _api.CreateWalletAsync("EUR", 20m);

        using var response = await _api.AdjustAsync(wallet.Id, amount, currency, strategy, idempotencyKey);

        await response.ShouldBeErrorAsync(HttpStatusCode.BadRequest, "invalid_request");
        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(20m);
    }

    [Fact]
    public async Task AdjustsWithoutAnIdempotencyKeyExactlyAsTheAssignmentSpecifies()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 20m);

        // POST /api/wallets/{walletId}/adjustbalance?amount={amount}&currency={currency}&strategy={strategy}, no header
        using var response = await _api.AdjustAsync(wallet.Id, 5m, "EUR", "AddFundsStrategy", idempotencyKey: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Idempotent-Replayed").Should().BeFalse();
        (await response.Content.ReadFromJsonAsync<WalletDto>()).Should().Be(new WalletDto(wallet.Id, "EUR", 25m));
    }

    [Fact]
    public async Task AppliesEachIdenticalRequestWithoutAKey()
    {
        // Without a key there's nothing to recognise a retry by, so each request is a new adjustment
        var wallet = await _api.CreateWalletAsync("EUR", 20m);

        using var first = await _api.AdjustAsync(wallet.Id, 5m, "EUR", "AddFundsStrategy", idempotencyKey: null);
        using var second = await _api.AdjustAsync(wallet.Id, 5m, "EUR", "AddFundsStrategy", idempotencyKey: null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(30m);
    }

    [Fact]
    public async Task ReturnsNotFoundWhenAdjustingAnUnknownWallet()
    {
        using var response = await _api.AdjustAsync(long.MaxValue, 10m, "EUR", "AddFundsStrategy");

        await response.ShouldBeErrorAsync(HttpStatusCode.NotFound, "wallet_not_found");
    }

    private async Task AssertAdjustedAsync(long walletId, decimal amount, string strategy, decimal expectedBalance)
    {
        using var response = await _api.AdjustAsync(walletId, amount, "EUR", strategy);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<WalletDto>()).Should().Be(new WalletDto(walletId, "EUR", expectedBalance));
    }
}
