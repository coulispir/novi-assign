using System;

namespace Ecb.Gateway;

/// <summary>
/// Settings for <see cref="EcbClient"/>. The host binds them from configuration (e.g. an <c>Ecb</c> section) and
/// validates them at startup.
/// </summary>
public sealed class EcbClientOptions
{
    /// <summary>
    /// The daily reference rates feed, e.g. https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml.
    /// </summary>
    public Uri? DailyRatesUrl { get; init; }

    /// <summary>
    /// How long one request to the feed may take before it's abandoned.
    /// </summary>
    public TimeSpan Timeout { get; init; }
}
