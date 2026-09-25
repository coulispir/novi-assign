using System;

namespace Core.Service.Entities;

// Stores the outcome of a balance adjustment so a retried request with the same key replays it instead of re-applying it
public class IdempotencyRecord
{
    public const int MaxKeyLength = 100;

    public string Key { get; private set; } = string.Empty;

    // SHA-256 of the request parameters; detects a key being reused for a different request
    public string RequestHash { get; private set; } = string.Empty;

    // Snapshot of the response returned by the original request
    public long WalletId { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public decimal Balance { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private IdempotencyRecord() { }

    public static IdempotencyRecord Create(string key, string requestHash, AccountWallet wallet)
    {
        return new IdempotencyRecord
        {
            Key = key,
            RequestHash = requestHash,
            WalletId = wallet.Id,
            Currency = wallet.Currency,
            Balance = wallet.Balance,
            CreatedAt = DateTime.UtcNow
        };
    }
}
