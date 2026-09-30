using System;
using System.Threading.Tasks;

using App.Host.Infrastructure;
using App.Host.Infrastructure.Caching;
using App.Host.Infrastructure.Ecb;
using App.Host.Infrastructure.Errors;
using App.Host.Infrastructure.HealthChecks;
using App.Host.Infrastructure.Jobs;
using App.Host.Infrastructure.RateLimiting;

using Core.Service.Data;
using Core.Service.Handlers;
using Core.Service.Interfaces;
using Core.Service.Repositories;
using Core.Service.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;

using Wallet.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Injected through environment variables (Docker Compose locally, a secret store in production)
var connectionString = builder.Configuration.GetRequiredConnectionString("DefaultConnection");

// Strategies are stateless; they must share the factory's singleton lifetime
builder.Services.AddSingleton<Core.Service.Strategies.IBalanceStrategy, Core.Service.Strategies.AddFundsStrategy>();
builder.Services.AddSingleton<Core.Service.Strategies.IBalanceStrategy, Core.Service.Strategies.SubtractFundsStrategy>();
builder.Services.AddSingleton<Core.Service.Strategies.IBalanceStrategy, Core.Service.Strategies.ForceSubtractFundsStrategy>();
builder.Services.AddSingleton<Core.Service.Strategies.IBalanceStrategyFactory, Core.Service.Strategies.BalanceStrategyFactory>();

builder.Services.AddDbContext<SystemDbContext>(options =>
    options.UseSqlServer(connectionString, sqlOptions =>
    {
        // Retries short network blips and other transient SQL errors
        sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null);

        // The migrations live in Core.Service, next to the DbContext
        sqlOptions.MigrationsAssembly("Core.Service");
    }));

// The standalone ECB client (feed URL and timeout from "Ecb") and the core's IEcbGateway port over it
builder.Services.AddEcbGateway(builder.Configuration);

// Repositories, services and one handler per use case. All scoped, because they share the request's DbContext
builder.Services.AddScoped<ICurrencyValueRepository, CurrencyValueRepository>();
builder.Services.AddScoped<ICurrencyRatesProvider, CurrencyRatesProvider>();
builder.Services.AddScoped<IEcbRatesService, EcbRatesService>();
builder.Services.AddScoped<IWalletRepository, WalletRepository>();
builder.Services.AddScoped<ICreateWalletHandler, CreateWalletHandler>();
builder.Services.AddScoped<IGetBalanceHandler, GetBalanceHandler>();
builder.Services.AddScoped<IAdjustBalanceHandler, AdjustBalanceHandler>();

// Sync ECB rates on startup and then every "EcbSync:Interval", on one node of the Quartz cluster at a time
builder.Services.AddEcbSyncJob(builder.Configuration, connectionString);

// The one Redis connection that the health check, the rates cache and rate limiting all share
builder.Services.AddRedis(builder.Configuration);

// /health (SQL Server + Redis) for the load balancer and readiness probes, /health/live for liveness probes
builder.Services.AddDependencyHealthChecks(builder.Configuration, connectionString);
builder.Services.AddCurrencyRatesCache(builder.Configuration);

// The real client IP behind the load balancer, which the per-IP rate limits depend on
builder.Services.AddTrustedForwardedHeaders(builder.Configuration);
builder.Services.AddClientIpRateLimiting(builder.Configuration, Wallet.Api.RateLimitPolicies.All);

// The controllers from Wallet.Api, with every error returned in the same format
builder.Services.AddWalletApi();

// OpenAPI document generated from the controllers, browsable through Swagger UI in Development
builder.Services.AddApiDocumentation();

// Safety net for exceptions thrown outside the controllers: the same error body, never a stack trace
builder.Services.AddGlobalExceptionHandling();

var app = builder.Build();

// Resolve the strategy factory now: it verifies every BalanceStrategyType has exactly one strategy, so a missing
// registration stops the host at startup instead of failing a client's request
app.Services.GetRequiredService<Core.Service.Strategies.IBalanceStrategyFactory>();

// Outermost, so an exception anywhere in the pipeline is caught. Development keeps the detailed developer page;
// every other environment gets the same error body from HttpExceptionHandler, then GenericExceptionHandler.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler();
}

// Must run first so logging and rate limiting see the real client IP rather than the load balancer's
app.UseForwardedHeaders();
app.UseSerilogRequestLogging(options => options.GetLevel = (httpContext, _, exception) =>
    exception is null && httpContext.Response.StatusCode < 500 && DependencyHealthCheckExtensions.IsHealthCheckRequest(httpContext)
        ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Information);
app.UseRouting();

// After routing, so endpoint-specific policies ([EnableRateLimiting]) are resolved
app.UseRateLimiter();
app.MapControllers();
app.MapDependencyHealthChecks();

if (app.Environment.IsDevelopment())
{
    app.MapApiDocumentation();
}

// Create the database if it's missing and apply any pending migrations before taking requests
await ApplyDatabaseMigrationsAsync(app);

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

static async Task ApplyDatabaseMigrationsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();

    try
    {
        logger.LogInformation("Checking database state and applying migrations...");
        var context = services.GetRequiredService<SystemDbContext>();

        // SQL Server reports a missing catalog as login failure 18456/38 instead of "database does not exist",
        // so EF cannot auto-create FinancialSystemDb. Create it via master first, then migrate.
        await EnsureDatabaseExistsAsync(context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Database connection string is missing."));

        await context.Database.MigrateAsync();
        logger.LogInformation("Database migrations applied successfully.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred while migrating the database engine.");
        throw; // Don't start taking requests against a database that isn't up to date
    }
}

static async Task EnsureDatabaseExistsAsync(string applicationConnectionString)
{
    var source = new SqlConnectionStringBuilder(applicationConnectionString);
    var databaseName = source.InitialCatalog;
    if (string.IsNullOrWhiteSpace(databaseName))
        throw new InvalidOperationException("Connection string does not specify a database name.");

    var master = new SqlConnectionStringBuilder(applicationConnectionString)
    {
        InitialCatalog = "master"
    };

    var sanitizedName = databaseName.Replace("]", "]]");
    await using var connection = new SqlConnection(master.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"IF DB_ID(N'{sanitizedName}') IS NULL CREATE DATABASE [{sanitizedName}];";

    const int DatabaseAlreadyExistsError = 1801;
    try
    {
        await command.ExecuteNonQueryAsync();
    }
    catch (SqlException ex) when (ex.Number == DatabaseAlreadyExistsError)
    {
        // Another replica starting at the same time created it between our check and CREATE; nothing left to do
    }
}
