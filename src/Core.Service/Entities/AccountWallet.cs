using System;

namespace Core.Service.Entities;

public class AccountWallet
{
    public long Id { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public decimal Balance { get; private set; }
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>(); // RowVersion column used for optimistic concurrency protection
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private AccountWallet() { }

    public static AccountWallet Create(string currency, decimal initialBalance = 0)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a valid 3-letter ISO code.", nameof(currency));

        if (initialBalance < 0)
            throw new ArgumentException("Initial balance cannot be negative.", nameof(initialBalance));

        return new AccountWallet
        {
            Currency = currency.ToUpperInvariant(),
            Balance = initialBalance,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public void Credit(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Credit amount must be positive.", nameof(amount));

        Balance += amount;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Debit(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Debit amount must be positive.", nameof(amount));

        if (Balance - amount < 0)
            throw new InvalidOperationException("Insufficient funds to complete this transaction.");

        Balance -= amount;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ForceDebit(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Force debit amount must be positive.", nameof(amount));

        Balance -= amount; // Bypasses the negative check protection rule intentionally
        UpdatedAt = DateTime.UtcNow;
    }
}
