using System;

namespace Core.Service.Exceptions;

public class WalletNotFoundException : Exception
{
    public WalletNotFoundException(long walletId) : base($"Wallet ID '{walletId}' not found.")
    {
        WalletId = walletId;
    }

    public long WalletId { get; }
}
