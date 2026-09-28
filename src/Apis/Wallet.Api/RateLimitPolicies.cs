using System.Collections.Generic;

namespace Wallet.Api;

/// <summary>
/// Names of the rate limiting policies endpoints opt into via <c>[EnableRateLimiting]</c>. Each policy is a fixed-window
/// limit per client IP, tracked separately for every endpoint and shared across all nodes through Redis. The host
/// registers every policy in <see cref="All"/> and reads its limits from <c>RateLimiting:Policies:{name}</c>.
/// </summary>
public static class RateLimitPolicies
{
    public const string WalletRead = "wallet-read";
    public const string WalletCreate = "wallet-create";
    public const string WalletAdjust = "wallet-adjust";

    // Every policy used by an endpoint must be listed here, or the host will not register it
    public static IReadOnlyList<string> All { get; } = [WalletRead, WalletCreate, WalletAdjust];
}
