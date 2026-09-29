using System;

namespace App.Host.Infrastructure.Jobs;

/// <summary>
/// Settings for the ECB sync job, bound from <c>EcbSync</c>.
/// </summary>
public sealed class EcbSyncOptions
{
    public const string SectionName = "EcbSync";

    // No default on purpose: a missing value fails at startup
    public TimeSpan Interval { get; init; }

    internal bool IsValid() => Interval > TimeSpan.Zero;
}
