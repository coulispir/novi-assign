# AGENTS.md

Knowledge base for coding agents working in this repository. Read this before starting a task. [README.md](README.md) has the full reasoning behind each design choice; this file covers what you need to change code safely.

## What this is

A wallet management REST API: ASP.NET Core on .NET 10, SQL Server via EF Core, Redis, Quartz. Clients create wallets, adjust balances (idempotently) and read balances converted to other currencies using European Central Bank (ECB) rates.

It is built to run as **several replicas behind a load balancer**. Every design decision assumes that more than one node is running: state lives in SQL Server or Redis, never in process memory.

## Commands

```bash
dotnet build                      # must finish with 0 warnings: warnings are errors
dotnet test                       # all tests; Docker must be running (Testcontainers Redis and SQL Server)
dotnet test tests/Unit.Tests      # unit tests only, no Docker needed

cp .env.sample .env && docker compose up -d                                  # dev stack, hot reload, http://localhost:5000
docker compose -f docker-compose.yml -f docker-compose.lb.yml up -d --build  # nginx + 2 production-image replicas
tests/requests.sh                 # example requests against a running stack
tests/rate-limit.sh [base-url]    # exercises every rate limit

# New EF Core migration (needs the dotnet-ef tool; the design-time factory lives in Core.Service)
dotnet ef migrations add <Name> --project src/Core.Service
# Commands that connect (database update, migrations list) need the same connection string the app uses:
ConnectionStrings__DefaultConnection="Server=127.0.0.1,1433;Database=FinancialSystemDb;User Id=sa;Password=<DB_PASSWORD from .env>;TrustServerCertificate=True;" \
  dotnet ef database update --project src/Core.Service
```

Migrations are applied automatically at startup ([Program.cs](src/App.Host/Program.cs)), which also creates the database if it is missing.

## Solution layout and dependency rules

```
src/
  App.Host/          Composition root: Program.cs, DI, middleware, config, Redis/rate limiting/caching/forwarded headers
  Apis/Wallet.Api/   Controllers, response models, RateLimitPolicies (a class library loaded as an application part)
  Core.Service/      Domain and application logic: entities, handlers, services, strategies, repositories,
                     EF DbContext + migrations, Quartz jobs, interfaces for everything external
  Ecb.Gateway/       Standalone ECB client library: IEcbClient -> EcbDailyRates (no project references)
tests/
  Unit.Tests/        Domain/ (entities, strategies; no mocks) and Application/ (handler, services, job; NSubstitute)
  Integration.Tests/ References App.Host; real Redis via Testcontainers, in-memory TestServer
  Functional.Tests/  The real Program.cs end to end via WebApplicationFactory; SQL Server + Redis containers
```

Dependencies point inward, towards `Core.Service`:

- `Core.Service` has **no dependency on ASP.NET, Redis or HTTP**. External concerns are interfaces in [Core.Service/Interfaces](src/Core.Service/Interfaces) (`IEcbGateway`, `ICurrencyRatesCache`, ...) implemented elsewhere. Keep it that way: put a new Redis or HTTP implementation in `App.Host/Infrastructure` or a gateway project, not in `Core.Service`.
- `Wallet.Api` references only `Core.Service`. `App.Host` references everything and wires it up.
- `Ecb.Gateway` references **no project**: it is a reusable client for the ECB feed, with its own `IEcbClient` and models. [EcbGatewayAdapter](src/App.Host/Infrastructure/Ecb/EcbGatewayAdapter.cs) in `App.Host` maps it to the core's `IEcbGateway` port and adds the EUR base rate (the feed quotes against EUR and doesn't list it). Keep app concepts out of the gateway, and feed-format details out of the core.
- A new project must be added to [WalletSystem.slnx](WalletSystem.slnx) **and** to the `COPY *.csproj` restore layer in the [Dockerfile](Dockerfile), or the production image won't build.

## Request flow

```
WalletController (Wallet.Api)  ->  IWalletHandler (validates input, maps commands/queries)
                               ->  IWalletService (business logic, transactions, idempotency)
                               ->  IWalletRepository / SystemDbContext, IBalanceStrategyFactory, ICurrencyRatesProvider
```

Middleware order in `Program.cs` matters: `UseForwardedHeaders` (first, so the real client IP is known) -> `UseSerilogRequestLogging` -> `UseRouting` -> `UseRateLimiter` (after routing, so `[EnableRateLimiting]` resolves) -> `MapControllers`. The integration test host [RateLimitedApp.cs](tests/Integration.Tests/RateLimiting/RateLimitedApp.cs) mirrors this order; change both together.

## Endpoints

| Endpoint | Rate limit policy | Notes |
|---|---|---|
| `POST /api/wallets` body `{ "currency": "EUR", "initialBalance": 100 }` | `wallet-create` | 201 |
| `GET /api/wallets/{walletId}?currency=USD` | `wallet-read` | `currency` optional; converts via EUR |
| `POST /api/wallets/{walletId}/adjustbalance?amount=&currency=&strategy=` (+ optional `Idempotency-Key` header) | `wallet-adjust` | `currency` may differ from the wallet's: converted first. `Idempotent-Replayed: true` on replay |

**The OpenAPI contract** is generated from the controllers by the built-in `Microsoft.AspNetCore.OpenApi` and browsable with Swagger UI, **in Development only**: `/swagger` and `/openapi/v1.json` (set up in [ApiDocumentationExtensions.cs](src/App.Host/Infrastructure/ApiDocumentationExtensions.cs)). Nothing is hand-written, so keep the controllers descriptive:
- Declare **every** status an action can return with `[ProducesResponseType]`, errors with `Type = typeof(ErrorResponse)`. An undeclared status is missing from the contract clients generate code from.
- Bind inputs with explicit `[FromRoute]` / `[FromQuery]` / `[FromHeader(Name = ...)]` / `[FromBody]` and return typed models, so parameters and schemas appear in the document.
- A new endpoint or status code gets a check in [OpenApiDocumentTests](tests/Integration.Tests/OpenApi/OpenApiDocumentTests.cs).
- Don't add Swashbuckle's generator (`AddSwaggerGen`) alongside it: only its UI package is used.
- The spec is **OpenAPI 3.0** with numbers documented as plain `number`/`integer` (a schema transformer removes the "or string" .NET adds). Keep both: "number or string" schemas break Swagger UI's form and make generated clients use strings for amounts. `OpenApiDocumentTests` fails if one comes back.

## Errors

Every error body is `{ "error": "<message>", "code": "<stable code>" }` ([ErrorResponse.cs](src/Apis/Wallet.Api/Models/ErrorResponse.cs)). Controller actions have **no try/catch**: [ApiExceptionFilter](src/Apis/Wallet.Api/Filters/ApiExceptionFilter.cs), applied to `WalletController`, maps domain exceptions from [Core.Service/Exceptions](src/Core.Service/Exceptions):

| Exception | Status | `code` |
|---|---|---|
| `DomainValidationException` | 400 | `invalid_request` |
| `UnsupportedCurrencyException` | 400 | `unsupported_currency` |
| `WalletNotFoundException` | 404 | `wallet_not_found` |
| `ConcurrencyConflictException` | 409 | `concurrency_conflict` |
| `InsufficientFundsException` | 422 | `insufficient_funds` |
| `IdempotencyKeyReuseException` | 422 | `idempotency_key_reused` |
| rate limited (middleware, not the filter) | 429 | `rate_limited` |
| **anything else** | 500 | `internal_error`, generic message, exception logged |

Outside the controllers, `UseExceptionHandler()` (the outermost middleware outside Development) tries the `IExceptionHandler`s registered by `AddGlobalExceptionHandling()`, **in order**: [HttpExceptionHandler](src/App.Host/Infrastructure/Errors/HttpExceptionHandler.cs) (a `BadHttpRequestException` keeps its own 4xx), then [GenericExceptionHandler](src/App.Host/Infrastructure/Errors/GenericExceptionHandler.cs) (logged, generic 500). A new specific handler goes before the generic one, which takes everything. Keep domain-exception mapping in `ApiExceptionFilter`; these handlers are the safety net, and must never write exception messages for 500s.

Rules:
- A client mistake throws `DomainValidationException`, **not** `ArgumentException`. `ArgumentException` is for guard clauses (programming errors) and becomes a 500. Don't throw `KeyNotFoundException` or `InvalidOperationException` for expected outcomes either; use or add a domain exception.
- `Core.Service` never references HTTP status codes. The mapping lives only in the filter.
- Don't return exception messages from 500s, and don't reword or reuse `code` values: clients branch on them.
- Adding an error: exception class in `Core.Service/Exceptions` -> constant in `ErrorCodes` -> case in `ApiExceptionFilter` -> row in [ErrorResponseTests](tests/Integration.Tests/ErrorHandling/ErrorResponseTests.cs) -> a functional test asserting it with `ShouldBeErrorAsync` -> README "Errors" table.
- Model binding and validation failures (a missing parameter, an unparsable value, bad JSON, an unknown strategy) also return `ErrorResponse` with `invalid_request`, via `InvalidModelStateResponseFactory` in [AddWalletApi](src/Apis/Wallet.Api/WalletApiServiceCollectionExtensions.cs). **Register controllers only through `AddWalletApi()`** (in `Program.cs` and in every test host), or requests fall back to problem details.
- Required value-type query parameters use `[BindRequired]`. `[Required]` can't detect a missing value there: it silently binds the default, e.g. the first enum member.

## Domain invariants (don't break these)

**Money and currencies**
- Always `decimal`, never `double`. Balances are `decimal(18,4)`, rates `decimal(18,6)`. Converted balances are rounded to 4 places.
- Currency codes are 3-letter ISO, stored upper-case as `char(3)` (non-Unicode). Compare case-insensitively.
- All rates are **relative to EUR**. EUR is always 1 (the gateway injects it, and `CurrencyConverter` short-circuits it). Conversion is `amount / rate(from) * rate(to)`, rounded to 4 decimals.
- **All conversion goes through [CurrencyConverter](src/Core.Service/Services/CurrencyConverter.cs)**: balance display on `GET` and adjustments in another currency. Don't duplicate the maths.
- **Adjustments may be in any currency with a rate.** `WalletService` converts the amount to the wallet's currency *before* the strategy runs, so balance rules (no overdraft) apply in the wallet's currency. An amount that rounds to 0 after conversion is a `DomainValidationException`; a currency without a rate is an `UnsupportedCurrencyException`.

**Entities** ([Core.Service/Entities](src/Core.Service/Entities))
- Private setters, a private parameterless constructor for EF, a static `Create(...)` factory that validates, and behaviour methods (`Credit`, `Debit`, `ForceDebit`, `UpdateRate`) that set `UpdatedAt`. Don't add public setters; add a method.
- Timestamps are `DateTime.UtcNow`.
- EF mapping lives in `IEntityTypeConfiguration<T>` classes under `Data/Configurations`, discovered automatically.

**Balance adjustments**
- Strategies ([Core.Service/Strategies](src/Core.Service/Strategies)) implement `IBalanceStrategy` and declare their [BalanceStrategyType](src/Core.Service/Strategies/BalanceStrategyType.cs). `BalanceStrategyFactory` selects them by that enum, and they're registered as **singletons** in `Program.cs`. They must be stateless.
  - **The enum member names are the public contract** (`?strategy=AddFundsStrategy`, the OpenAPI `enum`, and the idempotency hash, which uses the lower-cased name). Never rename a member, and never use its numeric value for meaning.
  - The enum binds through `StrictEnumConverter` (names only, case-insensitive; numbers rejected) and serialises as names (`JsonStringEnumConverter`). Keep both attributes.
  - Adding a strategy: add the enum member, create the class, and register it in `Program.cs`. The factory throws at startup if a member has no strategy or two, and `BalanceStrategyTests` checks that every member has exactly one class. Then update `tests/requests.sh` and the README.
- `SubtractFundsStrategy` rejects going negative (through `AccountWallet.Debit`, which throws `InsufficientFundsException`); `ForceSubtractFundsStrategy` allows it on purpose (`ForceDebit`).
- `AccountWallet.RowVersion` is an optimistic concurrency token. Concurrent updates surface as `DbUpdateConcurrencyException`, which becomes `ConcurrencyConflictException` (409). Don't add locks around it. Any other `DbUpdateException` is rethrown and becomes a 500.

**Idempotency** ([WalletService.AdjustBalanceAsync](src/Core.Service/Services/WalletService.cs))
- `Idempotency-Key` is **optional**: the endpoint must keep working exactly as the assignment's URL, with no header. A missing or empty key → no idempotency (`AdjustWithoutIdempotencyAsync`: no lookup, hash or record; each request applies). A key that is sent must be 1-100 characters (`IdempotencyRecord.MaxKeyLength`) and not blank, or it's a `DomainValidationException` (400). Never make the header required again.
- The `IdempotencyRecord` is saved in **the same `SaveChanges`** as the balance change, so both commit or neither does. Keep that when changing this code.
- The request hash is SHA-256 over normalised `walletId|amount|CURRENCY|strategy`. The same key with the same request replays the stored result; the same key with a different request -> 422.
- On `DbUpdateException` the change tracker is cleared and the key is looked up again, because a parallel request may have won.

## Infrastructure patterns

**Redis**: exactly one shared `IConnectionMultiplexer`, registered by `AddRedis` ([RedisServiceCollectionExtensions.cs](src/App.Host/Infrastructure/RedisServiceCollectionExtensions.cs)) with `AbortOnConnectFail = false` and `BacklogPolicy.FailFast`. Any new Redis feature must resolve this instance, not open its own connection.

**Decorators**: cross-cutting behaviour wraps an interface instead of being added to its implementation. [LoggingEcbGatewayDecorator](src/Core.Service/Decorators/LoggingEcbGatewayDecorator.cs) times and logs `IEcbGateway`, and `FailOpenRateLimiter` makes the Redis limiter fail open. With the built-in container, register the inner type as itself and the interface as a factory that wraps it (see `AddEcbGateway`); there's no Scrutor or Autofac. A decorator must pass results and exceptions through unchanged. Tests that replace the interface (e.g. `WalletApiFactory` swapping in `FakeEcbFeed`) replace the whole chain.

**Health checks**: `/health` (readiness: SQL Server + Redis) and `/health/live` (liveness: no dependency checks), in [DependencyHealthCheckExtensions](src/App.Host/Infrastructure/HealthChecks/DependencyHealthCheckExtensions.cs). A dependency the app fails open on (Redis) must register with `HealthStatus.Degraded`, never `Unhealthy`. A new *required* dependency gets a check tagged `dependency` with the configured timeout. The response writer outputs statuses and durations only; never add descriptions or exception text to it.

**Fail open**: Redis is an optimisation, never a hard dependency. Rate limiting ([FailOpenRateLimiter.cs](src/App.Host/Infrastructure/RateLimiting/FailOpenRateLimiter.cs)) and the rates cache ([RedisCurrencyRatesCache.cs](src/App.Host/Infrastructure/Caching/RedisCurrencyRatesCache.cs)) catch `RedisException or RedisTimeoutException`, log a warning and carry on. New Redis code should do the same. The warning messages (`Rate limiter store is unavailable`, `Currency rates cache is unavailable`) are meant to be alerted on, so don't reword them casually.

**Redis keys**: version the key when the format changes (`currency-rates:latest:v1`), always set a TTL, and use `MULTI/EXEC` transactions for multi-step writes. Rate limit keys use hash tags for Redis Cluster; that is why route braces are stripped from the partition key.

**Currency rates**: `EcbSyncJob` runs every `EcbSync:Interval` (1 minute by default) on **one node** (Quartz clustered SQL Server job store, `[DisallowConcurrentExecution]`). It saves rates with **one raw SQL `MERGE`** ([CurrencyValueRepository.MergeRatesAsync](src/Core.Service/Repositories/CurrencyValueRepository.cs); the assignment requires this). It's keyed on `(CurrencyCode, RateDate)`, so it keeps one row per currency per date (the history). It then rebuilds the Redis snapshot from the database on every run, even when nothing changed. Reads go through `CurrencyRatesProvider`: Redis first; on a miss, the database, then `AddIfMissingAsync` (a `WATCH`-guarded fill that never overwrites a fresher snapshot). Don't add an in-process cache (`IMemoryCache`, `HybridCache`): other nodes would serve stale rates. The README explains why.

**Rate persistence rules**: don't write `CurrencyValues` through tracked EF entities (`Add` / `SaveChanges`); keep all rate writes in the single `MERGE`. Only ever pass values to it as typed `SqlParameter`s, never concatenate them into the SQL. Feed `MergeRatesAsync` validated, de-duplicated rows (one per `(CurrencyCode, RateDate)`), as `EcbRatesService` does. If a column of `CurrencyValues` changes, update the MERGE SQL too; [CurrencyRatesMergeTests](tests/Functional.Tests/CurrencyRatesMergeTests.cs) runs it against real SQL Server.

**Rate limiting**: fixed window per client IP per endpoint (HTTP method + route template), with counters in Redis. IPv6 is grouped by `/64`. Adding a policy:
1. Add a constant to [RateLimitPolicies.cs](src/Apis/Wallet.Api/RateLimitPolicies.cs) **and list it in `All`**.
2. Configure `RateLimiting:Policies:<name>` (`PermitLimit`, `Window` >= `00:00:01`) in `appsettings.json`.
3. Put `[EnableRateLimiting(RateLimitPolicies.X)]` on the endpoint.
4. Add limits for it in `RateLimitedApp` and update the README table.

**Client IP**: `X-Forwarded-For` is trusted only from `ForwardedHeaders:KnownProxies` / `KnownNetworks` (loopback by default). Never trust it more broadly, or clients can spoof their IP to get round rate limits.

**Configuration and options**
- **Never hard-code credentials**, not even in design-time or test code. The `dotnet ef` factory (`SystemDbContextFactory`) reads `ConnectionStrings__DefaultConnection` too, and its fallback has no credentials.
- Connection strings (`DefaultConnection`, `Redis`) come from environment variables (`ConnectionStrings__X`) and are read with `GetRequiredConnectionString`, which throws at startup if they are missing. Never commit real secrets.
- Options classes: `sealed`, a `SectionName` const, `init` properties with **no defaults** (missing config should fail), an `internal bool IsValid()`, registered with `.Bind(...).Validate(...).ValidateOnStart()` inside an `IServiceCollection` extension method (`AddXxx`) in `App.Host/Infrastructure`. Add a case to [StartupValidationTests.cs](tests/Integration.Tests/StartupValidationTests.cs), or to the feature's own configuration tests (e.g. [EcbConfigurationTests](tests/Integration.Tests/Gateways/EcbConfigurationTests.cs)).
- Don't hard-code tunables such as URLs, timeouts or intervals: add them to an options class and `appsettings.json`. A value needed *while services are registered*, such as the Quartz trigger interval, can't use `ValidateOnStart`. Read and validate it in the `AddXxx` method and throw a message naming the setting, as `AddEcbSyncJob` and `GetRequiredConnectionString` do.
- Options classes of a reusable library (e.g. `Ecb.Gateway`'s `EcbClientOptions`) live in the library and stay plain. The host binds and validates them.

**DI lifetimes**: services, repositories and handlers are scoped (they use the `DbContext`); strategies, the factory and the Redis cache are singletons. Outbound HTTP uses typed `AddHttpClient<TInterface, TImpl>`.

**Logging**: Serilog, configured from `appsettings.json`. Class libraries log through `ILogger<T>`; `Directory.Build.props` adds `Microsoft.Extensions.Logging.Abstractions` to them automatically. Use structured templates (`{ClientIp}`), not string interpolation.

## Code conventions

- Strict analysers: `AnalysisMode=All`, `AnalysisLevel=latest-strict`, `TreatWarningsAsErrors`, nullable enabled. Fix warnings; don't suppress them (only generated migration files contain `#pragma`).
- **Central package management**: versions live only in [Directory.Packages.props](Directory.Packages.props). In a `.csproj`, write `<PackageReference Include="X" />` with no version.
- File-scoped namespaces matching the folder. Explicit `using` directives even though implicit usings are on, `System.*` first, groups separated by blank lines.
- Private fields `_camelCase`, private static fields `s_camelCase`, constants PascalCase. Dependencies come in through constructors and are stored in `readonly` fields.
- Async all the way, with a `CancellationToken` passed through. Repositories and handlers return `ValueTask`/`Task` in line with the existing interface; `App.Host` infrastructure code uses `ConfigureAwait(false)`.
- Commands, queries and DTOs are `record`s. Response models live in `Wallet.Api/Models`.
- Comments explain **why** (concurrency, multi-node behaviour, trade-offs), not what. Public interfaces and infrastructure extension methods get `<summary>` doc comments. Match the density of the surrounding code.
- Formatting follows [.editorconfig](.editorconfig): 4-space indent for C#, 2 for XML/JSON, LF, final newline.

## Testing

Test each layer with the lightest setup that can still catch its bugs. The README's "Tests" section has the full breakdown.

- **Unit tests** (`tests/Unit.Tests`): xUnit + NSubstitute + FluentAssertions. `Domain/` tests entities and strategies with no mocks; `Application/` tests the handler, services and job with ports mocked. Assert the exact domain exception type each rule throws. Mock interfaces, not `DbContext`. Names follow `Method_Scenario_Result`.
- **Integration tests** (`tests/Integration.Tests`): infrastructure adapters. Redis tests join `[Collection(RedisCollection.Name)]` to share the Testcontainers Redis ([RedisFixture.cs](tests/Integration.Tests/RedisFixture.cs)). Tests in that collection run one at a time, so isolate state: rate limit tests take a fresh IP from `RateLimitedApp.NextClientIp()`, and cache tests reset their key first. They register the **production** extension methods (`AddRedis`, `AddClientIpRateLimiting`, ...) rather than rebuilding them, and substitute the wallet handler, so there is no SQL Server here.
- **Error handling tests** ([ErrorResponseTests](tests/Integration.Tests/ErrorHandling/ErrorResponseTests.cs)): the real controller on a TestServer with a throwing handler substitute. No Redis needed.
- **Functional tests** (`tests/Functional.Tests`): the real `Program.cs` against SQL Server and Redis containers, through [WalletApiFactory](tests/Functional.Tests/Infrastructure/WalletApiFactory.cs); only the ECB feed is faked ([FakeEcbFeed](tests/Functional.Tests/Infrastructure/FakeEcbFeed.cs)). The Quartz scheduler doesn't run: call `EcbSyncJob` explicitly when a test needs a sync. Every test creates its own wallets and never relies on another test's data. The tests keep **their own copies of the response contracts** (`WalletDto`, `ErrorDto`, ...) on purpose, so don't replace them with the API's types. Assert errors with `response.ShouldBeErrorAsync(status, "code")`.
- New behaviour needs tests at the matching level, including the Redis-down (fail-open) path for anything that touches Redis, and a functional test for anything that depends on SQL Server behaviour (row versions, unique keys).

## Documentation

The README is the design record and is kept up to date with every feature: how it works, configuration, trade-offs and production considerations. When you add or change behaviour, config keys, endpoints or policies, update the README (and `tests/requests.sh` / `tests/rate-limit.sh` if requests change) in the same change. Update this file when a convention or invariant changes.

## Git workflow

- Branch from `master` as `feature/<name>` or `chore/<name>`; PRs go to `master`.
- Commit messages: `feature: <Sentence.>` or `chore: <Sentence.>` (e.g. `feature: Add rate limit mechanism with Redis.`).
- Before opening a PR, run `dotnet build` (0 warnings) and `dotnet test`.
- CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs exactly that on `ubuntu-latest` (Release configuration), plus a build of the production Docker image. It is the source of truth for what a PR must pass, so keep it green. A new test project is picked up automatically once it's in `WalletSystem.slnx`. Container images used by tests must run on linux/amd64 runners.
