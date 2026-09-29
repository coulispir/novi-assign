using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Wallet.Api.Models;

namespace App.Host.Infrastructure.Errors;

/// <summary>
/// The catch-all, registered last: any exception no earlier handler took is a server fault. It's logged and answered
/// with a generic 500, never the exception's message or stack trace.
/// </summary>
internal sealed class GenericExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GenericExceptionHandler> _logger;

    public GenericExceptionHandler(ILogger<GenericExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // The client went away; there is nobody to answer and nothing went wrong on our side
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
            return true;

        _logger.LogError(exception, "Unhandled exception while processing {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);

        await WriteAsync(httpContext, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static Task WriteAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return httpContext.Response.WriteAsJsonAsync(new ErrorResponse("An unexpected error occurred.", ErrorCodes.InternalError), cancellationToken);
    }
}
