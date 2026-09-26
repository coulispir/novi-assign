using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Models;

namespace Core.Service.Interfaces;

public interface IEcbGateway
{
    /// <summary>
    /// Fetches and extracts the daily currency exchange benchmarks directly from the ECB feed.
    /// </summary>
    Task<IEnumerable<EcbRateResult>> FetchDailyRatesAsync(CancellationToken cancellationToken = default);
}
