using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Models;

namespace Core.Service.Interfaces;

public interface IEcbGateway
{
    /// <summary>
    /// Fetches the latest daily rates from the ECB, including EUR at 1.
    /// </summary>
    Task<IEnumerable<EcbRateResult>> FetchDailyRatesAsync(CancellationToken cancellationToken = default);
}
