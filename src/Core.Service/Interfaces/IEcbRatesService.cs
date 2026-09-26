using System.Threading;
using System.Threading.Tasks;

using Core.Service.Models;

namespace Core.Service.Interfaces;

public interface IEcbRatesService
{
    /// <summary>
    /// Pulls the latest ECB rates through the gateway and upserts them into the CurrencyValues store.
    /// </summary>
    Task<EcbSyncSummary> SyncLatestRatesAsync(CancellationToken cancellationToken = default);
}
