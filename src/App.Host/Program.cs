using System;
using System.Threading.Tasks;

using App.Host.Infrastructure;
using App.Host.Infrastructure.Caching;
using App.Host.Infrastructure.RateLimiting;

using Core.Service.Data;
using Core.Service.Handlers;
using Core.Service.Interfaces;
using Core.Service.Jobs;
using Core.Service.Repositories;
using Core.Service.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Quartz;

using Serilog;

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

// Register the SystemDbContext using SQL Server defaults
builder.Services.AddDbContext<SystemDbContext>(options =>
    options.UseSqlServer(connectionString, sqlOptions =>
    {
        // Resiliency strategy: automatically handles transient network drops
        sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null);

        // Tell EF Core that migrations live inside the Core.Service assembly, not here
        sqlOptions.MigrationsAssembly("Core.Service");
    }));

// Register the external integration gateway using HttpClient factory pattern rules
builder.Services.AddHttpClient<IEcbGateway, Ecb.Gateway.Services.EcbGateway>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Add("User-Agent", "WalletManagementSystem/1.0");
});

// Register application services and data repositories
builder.Services.AddScoped<ICurrencyValueRepository, CurrencyValueRepository>();
builder.Services.AddScoped<ICurrencyRatesProvider, CurrencyRatesProvider>();
builder.Services.AddScoped<IEcbRatesService, EcbRatesService>();
builder.Services.AddScoped<IWalletRepository, WalletRepository>();
builder.Services.AddScoped<IWalletService, WalletService>();
builder.Services.AddScoped<IWalletHandler, WalletHandler>();

// Schedule the ECB rate synchronization job to run on startup and every minute thereafter
builder.Services.AddQuartz(q =>
{
    // Nodes join the same cluster by sharing the (default) scheduler name; each needs a unique instance id
    q.UseInstanceIdGenerator<UniqueNodeInstanceIdGenerator>();

    // Clustered SQL Server job store: each trigger fire is acquired by exactly one node, and
    // [DisallowConcurrentExecution] is enforced cluster-wide. If a node dies, another one takes over.
    q.UsePersistentStore(store =>
    {
        store.UseSqlServer(connectionString);
        store.UseSystemTextJsonSerializer();
        store.UseClustering();
        store.ProvisionSchema(); // Creates the QRTZ_* tables on first startup if they are missing
    });

    q.AddJob<EcbSyncJob>(opts => opts.WithIdentity(EcbSyncJob.Key));

    q.AddTrigger(opts => opts
        .ForJob(EcbSyncJob.Key)
        .WithIdentity($"{nameof(EcbSyncJob)}-trigger")
        .StartNow()
        .WithSimpleSchedule(schedule => schedule
            .WithInterval(TimeSpan.FromMinutes(1))
            .RepeatForever()));
});

builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

// Shared Redis connection, the currency rates cache and rate limiting backed by it, and real client IP resolution behind load balancers
builder.Services.AddRedis(builder.Configuration);
builder.Services.AddCurrencyRatesCache(builder.Configuration);
builder.Services.AddTrustedForwardedHeaders(builder.Configuration);
builder.Services.AddClientIpRateLimiting(builder.Configuration, Wallet.Api.RateLimitPolicies.All);

// Add API Routing Controllers capability and dynamically discover external modules
builder.Services.AddControllers()
    .AddApplicationPart(typeof(Wallet.Api.AssemblyReference).Assembly);

// OpenAPI document generated from the controllers, browsable through Swagger UI in Development
builder.Services.AddApiDocumentation();

var app = builder.Build();

// Configure HTTP Request Pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Must run first so logging and rate limiting see the real client IP rather than the load balancer's
app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseRouting();

// After routing, so endpoint-specific policies ([EnableRateLimiting]) are resolved
app.UseRateLimiter();
app.MapControllers();

if (app.Environment.IsDevelopment())
{
    app.MapApiDocumentation();
}

// AUTOMATED DB INITIALIZATION AUTOMATION
// Creates database and applies migrations on startup if they don't exist yet
await ApplyDatabaseMigrationsAsync(app);

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

// Scoped lifecycle management method for database migrations
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

        // This will block execution until the SQL Server container accepts the schema,
        // matching the health check lifecycle setup inside your Docker Compose file.
        await context.Database.MigrateAsync();
        logger.LogInformation("Database migrations applied successfully.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred while migrating the database engine.");
        throw; // Prevent application from running in a broken state
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
