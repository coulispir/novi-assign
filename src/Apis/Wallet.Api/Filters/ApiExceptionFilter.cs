using System;

using Core.Service.Exceptions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

using Wallet.Api.Models;

namespace Wallet.Api.Filters;

/// <summary>
/// Turns exceptions thrown by wallet actions into <see cref="ErrorResponse"/> bodies. Expected domain failures map to
/// their own 4xx status and code. Anything else is a server fault: it is logged and returned as a generic 500, so
/// clients are never blamed for it and internal details (e.g. SQL errors) never leak into the response.
/// </summary>
public sealed class ApiExceptionFilter : IExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The client went away; there is nobody to answer and nothing went wrong on our side
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
            return;

        var (status, code) = context.Exception switch
        {
            DomainValidationException => (StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest),
            UnsupportedCurrencyException => (StatusCodes.Status400BadRequest, ErrorCodes.UnsupportedCurrency),
            WalletNotFoundException => (StatusCodes.Status404NotFound, ErrorCodes.WalletNotFound),
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, ErrorCodes.ConcurrencyConflict),
            InsufficientFundsException => (StatusCodes.Status422UnprocessableEntity, ErrorCodes.InsufficientFunds),
            IdempotencyKeyReuseException => (StatusCodes.Status422UnprocessableEntity, ErrorCodes.IdempotencyKeyReused),
            _ => (StatusCodes.Status500InternalServerError, ErrorCodes.InternalError),
        };

        string message;

        if (status == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(context.Exception, "Unhandled exception while processing {Method} {Path}", context.HttpContext.Request.Method, context.HttpContext.Request.Path);
            message = "An unexpected error occurred.";
        }
        else
        {
            message = context.Exception.Message;
        }

        context.Result = new ObjectResult(new ErrorResponse(message, code)) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
