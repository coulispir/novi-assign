using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using App.Host.Infrastructure;

using Core.Service.Handlers;

using FluentAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Wallet.Api;

namespace Integration.Tests.OpenApi;

/// <summary>
/// Generates the OpenAPI document from the real <c>WalletController</c> with the production registrations, and checks
/// that the contract clients see is complete: every endpoint, the idempotency header and the error responses.
/// </summary>
public sealed class OpenApiDocumentTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private JsonElement _document;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(Substitute.For<IWalletHandler>());
        builder.Services.AddWalletApi();
        builder.Services.AddApiDocumentation();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapControllers();
        _app.MapApiDocumentation();
        await _app.StartAsync();

        _client = _app.GetTestClient();

        using var response = await _client.GetAsync(new Uri(ApiDocumentationExtensions.DocumentPath, UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public void IsAnOpenApi30DocumentForTheWalletApi()
    {
        _document.GetProperty("openapi").GetString().Should().StartWith("3.0");
        _document.GetProperty("info").GetProperty("title").GetString().Should().Be("Wallet API");
    }

    [Fact]
    public void GivesEverySchemaASingleType()
    {
        // "number or string" schemas make Swagger UI reject every value for that parameter as missing
        FindTypeLists(_document, "$").Should().BeEmpty();
    }

    [Fact]
    public void DescribesTheAdjustmentAmountAsARequiredNumber()
    {
        var amount = _document.GetProperty("paths").GetProperty("/api/wallets/{walletId}/adjustbalance").GetProperty("post")
            .GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "amount");

        amount.GetProperty("required").GetBoolean().Should().BeTrue();
        amount.GetProperty("schema").GetProperty("type").GetString().Should().Be("number");
    }

    [Fact]
    public void DescribesEveryWalletEndpoint()
    {
        var paths = _document.GetProperty("paths");

        paths.GetProperty("/api/wallets").TryGetProperty("post", out _).Should().BeTrue();
        paths.GetProperty("/api/wallets/{walletId}").TryGetProperty("get", out _).Should().BeTrue();
        paths.GetProperty("/api/wallets/{walletId}/adjustbalance").TryGetProperty("post", out _).Should().BeTrue();
    }

    [Fact]
    public void AdjustBalanceDocumentsTheIdempotencyKeyHeaderAndEveryResponse()
    {
        var adjust = _document.GetProperty("paths").GetProperty("/api/wallets/{walletId}/adjustbalance").GetProperty("post");

        var idempotencyKey = adjust.GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Idempotency-Key");
        idempotencyKey.GetProperty("in").GetString().Should().Be("header");
        // Optional: the assignment's URL has no header, so the endpoint must work without it
        (idempotencyKey.TryGetProperty("required", out var required) && required.GetBoolean()).Should().BeFalse();

        adjust.GetProperty("responses").EnumerateObject().Select(r => r.Name)
            .Should().BeEquivalentTo("200", "400", "404", "409", "422", "429", "500");
    }

    [Fact]
    public void ListsTheStrategiesAsARequiredStringEnum()
    {
        var strategy = _document.GetProperty("paths").GetProperty("/api/wallets/{walletId}/adjustbalance").GetProperty("post")
            .GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "strategy");

        strategy.GetProperty("required").GetBoolean().Should().BeTrue();
        var schema = Resolve(strategy.GetProperty("schema"));
        schema.GetProperty("type").GetString().Should().Be("string");
        schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("AddFundsStrategy", "SubtractFundsStrategy", "ForceSubtractFundsStrategy");
    }

    // Enum schemas may be inlined or referenced from components
    private JsonElement Resolve(JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference)) return schema;

        var name = reference.GetString()!.Split('/')[^1];
        return _document.GetProperty("components").GetProperty("schemas").GetProperty(name);
    }

    [Fact]
    public void DescribesTheErrorResponseBody()
    {
        var properties = _document.GetProperty("components").GetProperty("schemas").GetProperty("ErrorResponse").GetProperty("properties");

        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("error", "code");
    }

    [Fact]
    public void DocumentsEveryErrorOfEveryEndpointWithTheErrorResponseBody()
    {
        // Includes 429, which the rate limiter writes rather than the controller
        var errors = _document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .SelectMany(operation => operation.Value.GetProperty("responses").EnumerateObject())
            .Where(response => response.Name[0] is '4' or '5')
            .ToList();

        errors.Should().NotBeEmpty();
        errors.Should().OnlyContain(response => response.Value.GetRawText().Contains("#/components/schemas/ErrorResponse", StringComparison.Ordinal));
    }

    private static List<string> FindTypeLists(JsonElement element, string path)
    {
        var found = new List<string>();

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                // 3.1 writes "number or string" as a type list, 3.0 as anyOf; Swagger UI can bind neither
                if ((property.Name == "type" && property.Value.ValueKind == JsonValueKind.Array) || property.Name == "anyOf")
                    found.Add($"{path}.{property.Name}");
                else
                    found.AddRange(FindTypeLists(property.Value, $"{path}.{property.Name}"));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                found.AddRange(FindTypeLists(item, $"{path}[{index++}]"));
        }

        return found;
    }

    [Fact]
    public async Task ServesSwaggerUi()
    {
        using var response = await _client.GetAsync(new Uri("/swagger/index.html", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
