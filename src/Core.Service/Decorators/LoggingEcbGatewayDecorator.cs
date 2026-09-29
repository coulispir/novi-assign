using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Interfaces;
using Core.Service.Models;

using Microsoft.Extensions.Logging;

namespace Core.Service.Decorators;

/// <summary>
/// Decorator that times every call to the ECB feed and logs its outcome, without the gateway implementation or its
/// callers knowing: the host registers it as <see cref="IEcbGateway"/> around the real gateway. Results and
/// exceptions pass through unchanged.
/// </summary>
public sealed class LoggingEcbGatewayDecorator : IEcbGateway
{
    private readonly IEcbGateway _inner;
    private readonly ILogger<LoggingEcbGatewayDecorator> _logger;

    public LoggingEcbGatewayDecorator(IEcbGateway inner, ILogger<LoggingEcbGatewayDecorator> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IEnumerable<EcbRateResult>> FetchDailyRatesAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var rates = (await _inner.FetchDailyRatesAsync(cancellationToken).ConfigureAwait(false)).ToList();

            _logger.LogInformation(
                "Fetched {RateCount} rates from the ECB feed for {RateDate:yyyy-MM-dd} in {ElapsedMilliseconds} ms",
                rates.Count,
                rates.Count > 0 ? rates[0].RateDate : null,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            return rates;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down, not failing
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ECB feed request failed after {ElapsedMilliseconds} ms", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}
