using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace App.Host.Infrastructure;

public static class ApiDocumentationExtensions
{
    public const string DocumentPath = "/openapi/v1.json";

    /// <summary>
    /// Generates the OpenAPI document from the controllers' routes, parameters and <c>[ProducesResponseType]</c>
    /// attributes, using the built-in ASP.NET Core generator.
    /// </summary>
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            // Client generators (e.g. openapi-generator for Kotlin) support 3.0 more reliably than the 3.1 default
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;

            // The web JSON defaults also read numbers from strings, so every number is generated as "number or string"
            // (a type list in 3.1, anyOf in 3.0). Swagger UI can't bind either and rejects every value as missing, and
            // generated clients would type amounts as strings. Document numbers as plain numbers; the API still accepts both.
            options.AddSchemaTransformer((schema, _, _) =>
            {
                if (schema.Type is { } type
                    && type.HasFlag(JsonSchemaType.String)
                    && (type.HasFlag(JsonSchemaType.Number) || type.HasFlag(JsonSchemaType.Integer)))
                {
                    schema.Type = type & ~JsonSchemaType.String;
                    schema.Pattern = null; // Only there to constrain the string form
                }

                // Enums serialised as names (e.g. BalanceStrategyType) are generated as a bare value list without a type.
                // Declare it, so generators produce a string enum rather than an untyped value.
                if (schema.Type is null
                    && schema.Enum is { Count: > 0 } values
                    && values.All(value => value?.GetValueKind() == JsonValueKind.String))
                {
                    schema.Type = JsonSchemaType.String;
                }

                return Task.CompletedTask;
            });

            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Wallet API";
                document.Info.Description = "Create wallets, adjust balances idempotently and read balances converted with ECB exchange rates.";
                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>
    /// Serves the OpenAPI document at <see cref="DocumentPath"/> and Swagger UI at <c>/swagger</c>.
    /// The host maps these only in Development, so production doesn't publish its API surface.
    /// </summary>
    public static WebApplication MapApiDocumentation(this WebApplication app)
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options => options.SwaggerEndpoint(DocumentPath, "Wallet API"));

        return app;
    }
}
