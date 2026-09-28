using System;

namespace App.Host.Infrastructure.RateLimiting;

/// <summary>
/// Limits for one named rate limiting policy, bound from <c>RateLimiting:Policies:{policyName}</c>: each client IP
/// may make at most <see cref="PermitLimit"/> requests to an endpoint using the policy within every <see cref="Window"/>.
/// </summary>
public sealed class RateLimitPolicyOptions
{
    public const string SectionName = "RateLimiting:Policies";

    // The Redis fixed window works at one-second resolution
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(1);

    // No defaults on purpose: a policy missing from configuration fails validation at startup
    public int PermitLimit { get; init; }

    public TimeSpan Window { get; init; }

    internal bool IsValid() => PermitLimit > 0 && Window >= MinimumWindow;
}
