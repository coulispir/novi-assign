using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using FluentAssertions;

using Functional.Tests.Infrastructure;

namespace Functional.Tests;

/// <summary>
/// These guarantees rest on SQL Server itself (the wallet row version and the idempotency key primary key), which is
/// why they are tested against a real database rather than a mocked or in-memory one.
/// </summary>
[Collection(WalletApiCollection.Name)]
public sealed class IdempotencyAndConcurrencyTests
{
    private const string ReplayedHeader = "Idempotent-Replayed";
    private const int ParallelRequests = 10;

    private readonly WalletApiClient _api;

    public IdempotencyAndConcurrencyTests(WalletApiFactory factory)
    {
        _api = new WalletApiClient(factory.CreateClient());
    }

    [Fact]
    public async Task RetryingWithTheSameKeyReplaysTheOriginalResultAndAppliesTheAdjustmentOnce()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 100m);
        var key = WalletApiClient.NewIdempotencyKey();

        using var first = await _api.AdjustAsync(wallet.Id, 25m, "EUR", "AddFundsStrategy", key);
        using var retry = await _api.AdjustAsync(wallet.Id, 25m, "EUR", "AddFundsStrategy", key);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.Contains(ReplayedHeader).Should().BeFalse();

        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.Headers.GetValues(ReplayedHeader).Should().Equal("true");
        (await retry.Content.ReadFromJsonAsync<WalletDto>()).Should().Be(await first.Content.ReadFromJsonAsync<WalletDto>());

        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(125m);
    }

    [Fact]
    public async Task ReplaysEvenAfterTheBalanceChangedSince()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 100m);
        var key = WalletApiClient.NewIdempotencyKey();

        using var first = await _api.AdjustAsync(wallet.Id, 25m, "EUR", "AddFundsStrategy", key);
        using var other = await _api.AdjustAsync(wallet.Id, 50m, "EUR", "AddFundsStrategy");
        using var retry = await _api.AdjustAsync(wallet.Id, 25m, "EUR", "AddFundsStrategy", key);

        // The replay returns the balance as it was right after the original request, not the current one
        (await retry.Content.ReadFromJsonAsync<WalletDto>())!.Balance.Should().Be(125m);
        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(175m);
    }

    [Fact]
    public async Task ReusingAKeyForADifferentRequestIsRejected()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 100m);
        var key = WalletApiClient.NewIdempotencyKey();

        using var first = await _api.AdjustAsync(wallet.Id, 25m, "EUR", "AddFundsStrategy", key);
        using var reused = await _api.AdjustAsync(wallet.Id, 30m, "EUR", "AddFundsStrategy", key);

        await reused.ShouldBeErrorAsync(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(125m);
    }

    [Fact]
    public async Task ConcurrentRetriesWithTheSameKeyApplyTheAdjustmentExactlyOnce()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 100m);
        var key = WalletApiClient.NewIdempotencyKey();

        var responses = await SendInParallelAsync(() => _api.AdjustAsync(wallet.Id, 10m, "EUR", "AddFundsStrategy", key));

        try
        {
            responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
            responses.Count(r => !r.Headers.Contains(ReplayedHeader)).Should().Be(1);
        }
        finally
        {
            DisposeAll(responses);
        }

        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(110m);
    }

    [Fact]
    public async Task ConcurrentAdjustmentsNeverLoseAnUpdate()
    {
        var wallet = await _api.CreateWalletAsync("EUR", 100m);

        // Different keys: each request is a separate adjustment racing on the same wallet row
        var responses = await SendInParallelAsync(() => _api.AdjustAsync(wallet.Id, 10m, "EUR", "AddFundsStrategy"));

        int applied;
        try
        {
            // Losers of the row version race get 409 and may retry; nothing else is acceptable
            responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
            applied = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        }
        finally
        {
            DisposeAll(responses);
        }

        applied.Should().BePositive();
        (await _api.GetBalanceAsync(wallet.Id)).OriginalBalance.Should().Be(100m + (10m * applied));
    }

    private static Task<HttpResponseMessage[]> SendInParallelAsync(System.Func<Task<HttpResponseMessage>> send) =>
        Task.WhenAll(Enumerable.Range(0, ParallelRequests).Select(_ => Task.Run(send)));

    private static void DisposeAll(HttpResponseMessage[] responses)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
