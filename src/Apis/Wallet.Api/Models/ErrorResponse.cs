namespace Wallet.Api.Models;

/// <summary>
/// Body of every error the API returns. <see cref="Code"/> is stable and meant for clients to branch on;
/// <see cref="Error"/> is a human-readable message that may change.
/// </summary>
public record ErrorResponse(string Error, string Code);

public static class ErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string UnsupportedCurrency = "unsupported_currency";
    public const string WalletNotFound = "wallet_not_found";
    public const string ConcurrencyConflict = "concurrency_conflict";
    public const string InsufficientFunds = "insufficient_funds";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string InternalError = "internal_error";
}
