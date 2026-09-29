using System.Linq;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

using Wallet.Api.Models;

namespace Wallet.Api;

public static class WalletApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the wallet controllers. Requests that fail model binding or validation (a missing required parameter,
    /// an unknown strategy, an unparsable amount) return the same <see cref="ErrorResponse"/> body as every other error,
    /// instead of ASP.NET Core's default problem details, so clients handle a single error shape.
    /// </summary>
    public static IMvcBuilder AddWalletApi(this IServiceCollection services)
    {
        return services
            .AddControllers()
            .AddApplicationPart(typeof(AssemblyReference).Assembly)
            .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context =>
            {
                var messages = context.ModelState
                    .Where(entry => entry.Value is { Errors.Count: > 0 })
                    // One message per parameter: the first is the specific one. An invalid value under [BindRequired]
                    // also gets a generic "was not provided" error, which would only repeat it.
                    .Select(entry => Describe(entry.Key, entry.Value!.Errors[0].ErrorMessage));

                return new BadRequestObjectResult(new ErrorResponse(string.Join(" ", messages), ErrorCodes.InvalidRequest));
            });
    }

    // Query-string errors such as "The value '0' is not valid." don't name the parameter, so prefix it
    private static string Describe(string parameter, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) message = "The value is invalid.";

        return string.IsNullOrEmpty(parameter) ? message : $"{parameter}: {message}";
    }
}
