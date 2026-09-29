using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

using App.Host.Infrastructure.Ecb;
using App.Host.Infrastructure.Jobs;

using Core.Service.Decorators;
using Core.Service.Interfaces;

using Ecb.Gateway;

using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Integration.Tests.Gateways;

/// <summary>
/// The ECB feed URL, HTTP timeout and sync interval come from configuration (Options pattern), and a bad value stops the
/// host at startup with a message naming the setting.
/// </summary>
public sealed class EcbConfigurationTests
{
    private static readonly Dictionary<string, string?> ValidSettings = new()
    {
        [$"{EcbServiceCollectionExtensions.SectionName}:DailyRatesUrl"] = "https://ecb.example/eurofxref-daily.xml",
        [$"{EcbServiceCollectionExtensions.SectionName}:Timeout"] = "00:00:07",
        [$"{EcbSyncOptions.SectionName}:Interval"] = "00:00:30",
    };

    [Fact]
    public void AppliesTheConfiguredUrlAndTimeoutToTheClient()
    {
        using var services = new ServiceCollection().AddLogging().AddEcbGateway(Configuration()).BuildServiceProvider();

        services.GetRequiredService<IOptions<EcbClientOptions>>().Value.DailyRatesUrl.Should().Be(new Uri("https://ecb.example/eurofxref-daily.xml"));
        // A typed client's HttpClient is registered under the service type's name
        services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IEcbClient)).Timeout.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void ResolvesTheGatewayPortToTheLoggingDecoratorAroundTheAdapter()
    {
        using var services = new ServiceCollection().AddLogging().AddEcbGateway(Configuration()).BuildServiceProvider();
        using var scope = services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IEcbGateway>().Should().BeOfType<LoggingEcbGatewayDecorator>();
    }

    public static TheoryData<string, string?, string> InvalidEcbSettings => new()
    {
        { "DailyRatesUrl", null, "DailyRatesUrl" },
        { "DailyRatesUrl", "/eurofxref-daily.xml", "DailyRatesUrl" },
        { "DailyRatesUrl", "ftp://ecb.example/rates.xml", "DailyRatesUrl" },
        { "Timeout", null, "Timeout" },
        { "Timeout", "00:00:00", "Timeout" },
    };

    [Theory]
    [MemberData(nameof(InvalidEcbSettings))]
    public async Task FailsToStartWithInvalidEcbSettings(string key, string? value, string setting)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddEcbGateway(Configuration(settings => settings[$"{EcbServiceCollectionExtensions.SectionName}:{key}"] = value));
        using var host = builder.Build();

        var start = () => host.StartAsync();

        (await start.Should().ThrowAsync<OptionsValidationException>()).WithMessage($"*'{EcbServiceCollectionExtensions.SectionName}:{setting}'*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00:00:00")]
    [InlineData("-00:01:00")]
    public void RejectsAMissingOrNonPositiveSyncInterval(string? interval)
    {
        var configuration = Configuration(settings => settings[$"{EcbSyncOptions.SectionName}:Interval"] = interval);

        var register = () => new ServiceCollection().AddEcbSyncJob(configuration, "Server=unused;Database=unused");

        register.Should().Throw<InvalidOperationException>().WithMessage($"*'{EcbSyncOptions.SectionName}:Interval'*");
    }

    [Fact]
    public void AcceptsAValidSyncInterval()
    {
        var register = () => new ServiceCollection().AddEcbSyncJob(Configuration(), "Server=unused;Database=unused");

        register.Should().NotThrow();
    }

    private static IConfiguration Configuration(Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>(ValidSettings);
        configure?.Invoke(settings);
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
