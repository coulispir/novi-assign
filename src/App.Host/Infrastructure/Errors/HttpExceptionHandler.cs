using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Wallet.Api.Models;

namespace App.Host.Infrastructure.Errors;

/// <summary>
/// Handles HTTP-level errors: requests the server itself rejected, such as a body over the size limit
/// (<see cref="BadHttpRequestException"/>). They keep their own 4xx status. Their message describes the request, not our
/// internals, so it's safe to return. Registered before <see cref="GenericExceptionHandler"/>, which takes everything else.
/// </summary>
internal sealed class HttpExceptionHandler : IExceptionHandler
{
    private readonly ILogger<HttpExceptionHandler> _logger;

    public HttpExceptionHandler(ILogger<HttpExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
            return false;

        _logger.LogWarning(exception, "Rejected malformed request {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = badRequest.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(new ErrorResponse(badRequest.Message, ErrorCodes.InvalidRequest), cancellationToken).ConfigureAwait(false);
        return true;
    }
}
