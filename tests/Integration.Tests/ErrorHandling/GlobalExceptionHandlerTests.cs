using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;

using App.Host.Infrastructure.Errors;

using FluentAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Wallet.Api.Models;

namespace Integration.Tests.ErrorHandling;

/// <summary>
/// Exceptions thrown outside the controllers (here: from middleware), with the production registration and
/// <c>UseExceptionHandler()</c> outermost, as in <c>Program.cs</c> outside Development.
/// </summary>
public sealed class GlobalExceptionHandlerTests : IAsyncLifetime
{
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddGlobalExceptionHandling();

        _app = builder.Build();
        _app.UseExceptionHandler();
        _app.Use((HttpContext context, Func<Task> next) => context.Request.Path.Value switch
        {
            "/middleware-failure" => throw new InvalidOperationException("Login failed for user 'sa' on 10.0.0.5."),
            "/too-large" => throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge),
            _ => next(),
        });
        _app.MapGet("/ok", () => "ok");

        await _app.StartAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task ReturnsAGeneric500ErrorResponseForAnExceptionOutsideTheControllers()
    {
        using var response = await _app.GetTestClient().GetAsync(new Uri("/middleware-failure", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadFromJsonAsync<ErrorResponse>()).Should().Be(new ErrorResponse("An unexpected error occurred.", ErrorCodes.InternalError));
    }

    [Fact]
    public async Task NeverLeaksTheExceptionMessageOrStackTrace()
    {
        using var response = await _app.GetTestClient().GetAsync(new Uri("/middleware-failure", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("sa").And.NotContain("10.0.0.5").And.NotContain("InvalidOperationException").And.NotContain(" at ");
    }

    [Fact]
    public async Task KeepsTheStatusOfARequestTheServerRejected()
    {
        using var response = await _app.GetTestClient().GetAsync(new Uri("/too-large", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadFromJsonAsync<ErrorResponse>()).Should().Be(new ErrorResponse("Request body too large.", ErrorCodes.InvalidRequest));
    }

    [Fact]
    public async Task TriesTheHttpHandlerBeforeTheGenericOne()
    {
        // The generic handler takes every exception, so a 413 surviving proves the HTTP handler ran first
        using var response = await _app.GetTestClient().GetAsync(new Uri("/too-large", UriKind.Relative));

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task LeavesSuccessfulRequestsAlone()
    {
        using var response = await _app.GetTestClient().GetAsync(new Uri("/ok", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
    }
}
