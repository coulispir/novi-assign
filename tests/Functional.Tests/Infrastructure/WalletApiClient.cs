using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using FluentAssertions;

namespace Functional.Tests.Infrastructure;

// The tests declare their own copies of the response contracts instead of reusing the API's types: renaming a property
// in the API must break these tests, because it breaks every client.
public sealed record WalletDto(long Id, string Currency, decimal Balance);

public sealed record BalanceDto(long WalletId, decimal OriginalBalance, string OriginalCurrency, decimal RequestedBalance, string RequestedCurrency);

public sealed record ErrorDto(string Error);

/// <summary>
/// Thin wrapper over the wallet endpoints, so tests read as API usage rather than HTTP plumbing.
/// </summary>
public sealed class WalletApiClient
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly HttpClient _http;

    public WalletApiClient(HttpClient http)
    {
        _http = http;
    }

    public Task<HttpResponseMessage> CreateAsync(string currency, decimal initialBalance) =>
        _http.PostAsJsonAsync(new Uri("/api/wallets", UriKind.Relative), new { currency, initialBalance });

    public async Task<WalletDto> CreateWalletAsync(string currency, decimal initialBalance)
    {
        using var response = await CreateAsync(currency, initialBalance);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<WalletDto>())!;
    }

    public Task<HttpResponseMessage> GetAsync(long walletId, string? currency = null)
    {
        var query = currency is null ? string.Empty : $"?currency={currency}";
        return _http.GetAsync(new Uri($"/api/wallets/{walletId}{query}", UriKind.Relative));
    }

    public async Task<BalanceDto> GetBalanceAsync(long walletId, string? currency = null)
    {
        using var response = await GetAsync(walletId, currency);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<BalanceDto>())!;
    }

    public Task<HttpResponseMessage> AdjustAsync(long walletId, decimal amount, string currency, string strategy, string? idempotencyKey)
    {
        var amountText = amount.ToString(CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/wallets/{walletId}/adjustbalance?amount={amountText}&currency={currency}&strategy={strategy}", UriKind.Relative));

        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyKeyHeader, idempotencyKey);
        }

        return _http.SendAsync(request);
    }

    public Task<HttpResponseMessage> AdjustAsync(long walletId, decimal amount, string currency, string strategy) =>
        AdjustAsync(walletId, amount, currency, strategy, NewIdempotencyKey());

    public static string NewIdempotencyKey() => Guid.NewGuid().ToString();
}
