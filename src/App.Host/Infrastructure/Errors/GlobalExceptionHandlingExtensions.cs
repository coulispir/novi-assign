using Microsoft.Extensions.DependencyInjection;

namespace App.Host.Infrastructure.Errors;

public static class GlobalExceptionHandlingExtensions
{
    /// <summary>
    /// Registers the handlers <c>app.UseExceptionHandler()</c> tries, in order, for exceptions thrown outside the controllers
    /// (inside actions, <c>ApiExceptionFilter</c> maps domain exceptions first):
    /// <see cref="HttpExceptionHandler"/> for HTTP-level errors, then <see cref="GenericExceptionHandler"/> for everything
    /// else. Order matters: the generic handler takes every exception, so it must be last.
    /// </summary>
    public static IServiceCollection AddGlobalExceptionHandling(this IServiceCollection services)
    {
        services.AddExceptionHandler<HttpExceptionHandler>();
        services.AddExceptionHandler<GenericExceptionHandler>();

        // UseExceptionHandler() refuses to start without a fallback for when no IExceptionHandler handles the exception.
        // GenericExceptionHandler always does, so this only satisfies that check, with the same body rather than
        // AddProblemDetails(), which would introduce a second error shape.
        services.AddExceptionHandler(options => options.ExceptionHandler = context => GenericExceptionHandler.WriteAsync(context));

        return services;
    }
}
