using System.Threading;
using System.Threading.Tasks;

using Ecb.Gateway.Models;

namespace Ecb.Gateway;

/// <summary>
/// Client for the European Central Bank's daily euro reference rates feed. Standalone: it depends on nothing but
/// <see cref="System.Net.Http.HttpClient"/>, so any application can use it.
/// </summary>
public interface IEcbClient
{
    /// <summary>
    /// Fetches and parses the latest daily rates. Throws <see cref="System.Net.Http.HttpRequestException"/> when the feed
    /// returns an error status, and <see cref="System.FormatException"/> when it contains no dated rates.
    /// </summary>
    Task<EcbDailyRates> GetDailyRatesAsync(CancellationToken cancellationToken = default);
}
