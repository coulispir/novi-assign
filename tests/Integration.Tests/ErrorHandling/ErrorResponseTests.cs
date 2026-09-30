using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Core.Service.Exceptions;
using Core.Service.Handlers;
using Core.Service.Strategies;

using FluentAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Wallet.Api;
using Wallet.Api.Models;

namespace Integration.Tests.ErrorHandling;

/// <summary>
/// Hosts the real <c>WalletController</c> in memory with handlers that throw, to check how each failure reaches the
/// client. Rate limiting is left out, so no Redis is needed.
/// </summary>
public sealed class ErrorResponseTests : IAsyncLifetime
{
    private readonly ICreateWalletHandler _createWallet = Substitute.For<ICreateWalletHandler>();
    private readonly IGetBalanceHandler _getBalance = Substitute.For<IGetBalanceHandler>();
    private readonly IAdjustBalanceHandler _adjustBalance = Substitute.For<IAdjustBalanceHandler>();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_createWallet);
        builder.Services.AddSingleton(_getBalance);
        builder.Services.AddSingleton(_adjustBalance);
        builder.Services.AddWalletApi();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapControllers();
        await _app.StartAsync();

        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    public static TheoryData<Exception, HttpStatusCode, string> DomainFailures => new()
    {
        { new DomainValidationException("Unknown strategy."), HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest },
        { new UnsupportedCurrencyException("No exchange rate."), HttpStatusCode.BadRequest, ErrorCodes.UnsupportedCurrency },
        { new WalletNotFoundException(42), HttpStatusCode.NotFound, ErrorCodes.WalletNotFound },
        { new ConcurrencyConflictException("Modified by another request."), HttpStatusCode.Conflict, ErrorCodes.ConcurrencyConflict },
        { new InsufficientFundsException("Not enough funds."), HttpStatusCode.UnprocessableEntity, ErrorCodes.InsufficientFunds },
        { new IdempotencyKeyReuseException("Key reused."), HttpStatusCode.UnprocessableEntity, ErrorCodes.IdempotencyKeyReused },
    };

    [Theory]
    [MemberData(nameof(DomainFailures))]
    public async Task MapsEachDomainFailureToItsStatusCodeAndCode(Exception exception, HttpStatusCode expectedStatus, string expectedCode)
    {
        _adjustBalance.HandleAsync(default!, default).ThrowsAsyncForAnyArgs(exception);

        using var response = await _client.SendAsync(AdjustBalance());

        response.StatusCode.Should().Be(expectedStatus);
        (await response.Content.ReadFromJsonAsync<ErrorResponse>()).Should().Be(new ErrorResponse(exception.Message, expectedCode));
    }

    [Fact]
    public async Task ReturnsAGeneric500WithoutLeakingDetailsForUnexpectedExceptions()
    {
        _getBalance.HandleAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("Login failed for user 'sa'."));

        using var response = await _client.GetAsync(new Uri("/api/wallets/1", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("sa");
        (await response.Content.ReadFromJsonAsync<ErrorResponse>()).Should().Be(new ErrorResponse("An unexpected error occurred.", ErrorCodes.InternalError));
    }

    [Fact]
    public async Task ArgumentExceptionsAreTreatedAsServerFaultsNotClientErrors()
    {
        // Only DomainValidationException means the client sent something invalid; a stray ArgumentException is a bug
        _createWallet.HandleAsync(default!, default).ThrowsAsyncForAnyArgs(new ArgumentNullException("currency"));

        using var response = await _client.PostAsJsonAsync(new Uri("/api/wallets", UriKind.Relative), new { currency = "EUR", initialBalance = 10 });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task SuccessfulRequestsAreUnaffected()
    {
        _adjustBalance.HandleAsync(default!, default).ReturnsForAnyArgs(new WalletAdjustmentResult(1, "EUR", 20m, IsReplay: true));

        using var response = await _client.SendAsync(AdjustBalance());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");
        (await response.Content.ReadFromJsonAsync<WalletResponse>()).Should().Be(new WalletResponse(1, "EUR", 20m));
    }

    public static TheoryData<string> InvalidStrategies => new()
    {
        "strategy=TransferStrategy",  // unknown name
        "strategy=0",                 // a number, even one that matches a member
        "strategy=42",                // a number no member has
        "strategy=",                  // empty
        "",                           // missing
    };

    [Theory]
    [MemberData(nameof(InvalidStrategies))]
    public async Task RejectsAnInvalidStrategyWithTheErrorResponseBodyBeforeReachingTheHandler(string strategyQuery)
    {
        using var response = await _client.SendAsync(AdjustBalance($"amount=10&currency=EUR&{strategyQuery}"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        error!.Code.Should().Be(ErrorCodes.InvalidRequest);
        error.Error.Should().Contain("strategy");
        await _adjustBalance.DidNotReceiveWithAnyArgs().HandleAsync(default!, default);
    }

    [Fact]
    public async Task NamesTheParameterAndReportsEachProblemOnce()
    {
        using var response = await _client.SendAsync(AdjustBalance("amount=10&currency=EUR&strategy=TransferStrategy"));

        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error
            .Should().Be("strategy: The value 'TransferStrategy' is not valid.");
    }

    [Theory]
    [InlineData("currency=EUR&strategy=AddFundsStrategy")]              // missing amount
    [InlineData("amount=ten&currency=EUR&strategy=AddFundsStrategy")]   // unparsable amount
    [InlineData("amount=10&strategy=AddFundsStrategy")]                 // missing currency
    public async Task RejectsMissingOrMalformedParametersWithTheErrorResponseBody(string query)
    {
        using var response = await _client.SendAsync(AdjustBalance(query));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Code.Should().Be(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task RejectsAMalformedJsonBodyWithTheErrorResponseBody()
    {
        using var content = new StringContent("{ \"currency\": ", System.Text.Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync(new Uri("/api/wallets", UriKind.Relative), content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Code.Should().Be(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task BindsTheStrategyNameIgnoringCase()
    {
        _adjustBalance.HandleAsync(default!, default).ReturnsForAnyArgs(new WalletAdjustmentResult(1, "EUR", 20m, IsReplay: false));

        using var response = await _client.SendAsync(AdjustBalance("amount=10&currency=EUR&strategy=forcesubtractfundsstrategy"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _adjustBalance.Received(1).HandleAsync(
            Arg.Is<AdjustBalanceCommand>(command => command.Strategy == BalanceStrategyType.ForceSubtractFundsStrategy),
            Arg.Any<System.Threading.CancellationToken>());
    }

    private static HttpRequestMessage AdjustBalance(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/wallets/1/adjustbalance?{query}", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return request;
    }

    private static HttpRequestMessage AdjustBalance()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/wallets/1/adjustbalance?amount=10&currency=EUR&strategy=SubtractFundsStrategy", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return request;
    }
}
