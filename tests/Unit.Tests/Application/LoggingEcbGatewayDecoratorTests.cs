using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Core.Service.Decorators;
using Core.Service.Interfaces;
using Core.Service.Models;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Unit.Tests.Application;

public sealed class LoggingEcbGatewayDecoratorTests
{
    private static readonly DateTime RateDate = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Unspecified);

    private readonly IEcbGateway _inner = Substitute.For<IEcbGateway>();
    private readonly CapturingLogger<LoggingEcbGatewayDecorator> _logger = new();
    private readonly LoggingEcbGatewayDecorator _decorator;

    public LoggingEcbGatewayDecoratorTests()
    {
        _decorator = new LoggingEcbGatewayDecorator(_inner, _logger);
    }

    [Fact]
    public async Task FetchDailyRatesAsync_ReturnsTheInnerGatewaysRatesUnchanged()
    {
        EcbRateResult[] rates = [new("USD", 1.1403m, RateDate), new("EUR", 1m, RateDate)];
        _inner.FetchDailyRatesAsync(Arg.Any<CancellationToken>()).Returns(rates);

        var result = await _decorator.FetchDailyRatesAsync();

        result.Should().Equal(rates);
    }

    [Fact]
    public async Task FetchDailyRatesAsync_LogsTheRateCountDateAndDuration()
    {
        _inner.FetchDailyRatesAsync(Arg.Any<CancellationToken>()).Returns([new EcbRateResult("USD", 1.1403m, RateDate)]);

        await _decorator.FetchDailyRatesAsync();

        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().StartWith("Fetched 1 rates from the ECB feed for 2026-09-25 in ").And.EndWith(" ms");
    }

    [Fact]
    public async Task FetchDailyRatesAsync_WhenTheFeedFails_LogsAWarningAndRethrowsTheSameException()
    {
        var failure = new HttpRequestException("ECB is down");
        _inner.FetchDailyRatesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(failure);

        var fetch = () => _decorator.FetchDailyRatesAsync();

        (await fetch.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(failure);
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task FetchDailyRatesAsync_WhenCancelled_RethrowsWithoutLoggingAFailure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _inner.FetchDailyRatesAsync(cancellation.Token).ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var fetch = () => _decorator.FetchDailyRatesAsync(cancellation.Token);

        await fetch.Should().ThrowAsync<OperationCanceledException>();
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchDailyRatesAsync_PassesTheCancellationTokenThrough()
    {
        using var cancellation = new CancellationTokenSource();
        _inner.FetchDailyRatesAsync(Arg.Any<CancellationToken>()).Returns([]);

        await _decorator.FetchDailyRatesAsync(cancellation.Token);

        await _inner.Received(1).FetchDailyRatesAsync(cancellation.Token);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
