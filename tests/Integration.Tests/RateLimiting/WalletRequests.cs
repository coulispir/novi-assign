using System;
using System.Net.Http;
using System.Net.Http.Json;

namespace Integration.Tests.RateLimiting;

/// <summary>
/// Valid requests for each rate-limited wallet endpoint, so only the rate limiter can turn them away.
/// </summary>
internal static class WalletRequests
{
    public static HttpRequestMessage GetBalance(long walletId = 1) =>
        new(HttpMethod.Get, new Uri($"/api/wallets/{walletId}", UriKind.Relative));

    public static HttpRequestMessage CreateWallet() =>
        new(HttpMethod.Post, new Uri("/api/wallets", UriKind.Relative))
        {
            Content = JsonContent.Create(new { currency = "EUR", initialBalance = 10 }),
        };

    public static HttpRequestMessage AdjustBalance()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/wallets/1/adjustbalance?amount=10&currency=EUR&strategy=AddFundsStrategy", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return request;
    }
}
