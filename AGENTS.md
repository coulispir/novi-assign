# AGENTS.md

Knowledge base for coding agents working in this repository. Read this before starting a task. [README.md](README.md) has the full reasoning behind each design choice; this file covers what you need to change code safely.

## What this is

A wallet management REST API: ASP.NET Core on .NET 10, SQL Server via EF Core, Redis, Quartz. Clients create wallets, adjust balances (idempotently) and read balances converted to other currencies using European Central Bank (ECB) rates.

It is built to run as **several replicas behind a load balancer**. Every design decision assumes that more than one node is running: state lives in SQL Server or Redis, never in process memory.

## Commands

```bash
dotnet build                      # must finish with 0 warnings: warnings are errors
dotnet test                       # unit + integration tests; Docker must be running (Testcontainers Redis)
dotnet test tests/Unit.Tests      # unit tests only, no Docker needed

cp .env.sample .env && docker compose up -d                                  # dev stack, hot reload, http://localhost:5000
docker compose -f docker-compose.yml -f docker-compose.lb.yml up -d --build  # nginx + 2 production-image replicas
tests/requests.sh                 # example requests against a running stack
tests/rate-limit.sh [base-url]    # exercises every rate limit

# New EF Core migration (needs the dotnet-ef tool; the design-time factory lives in Core.Service)
dotnet ef migrations add <Name> --project src/Core.Service
```

Migrations are applied automatically at startup ([Program.cs](src/App.Host/Program.cs)), which also creates the database if it is missing.

## Solution layout and dependency rules

```
src/
  App.Host/          Composition root: Program.cs, DI, middleware, config, Redis/rate limiting/caching/forwarded headers
  Apis/Wallet.Api/   Controllers, response models, RateLimitPolicies (a class library loaded as an application part)
  Core.Service/      Domain and application logic: entities, handlers, services, strategies, repositories,
                     EF DbContext + migrations, Quartz jobs, interfaces for everything external
  Ecb.Gateway/       HttpClient implementation of IEcbGateway (ECB daily XML feed)
tests/
  Unit.Tests/        References Core.Service only; NSubstitute mocks
  Integration.Tests/ References App.Host; real Redis via Testcontainers, in-memory TestServer
```

Dependencies point inward, towards `Core.Service`:

- `Core.Service` has **no dependency on ASP.NET, Redis or HTTP**. External concerns are interfaces in [Core.Service/Interfaces](src/Core.Service/Interfaces) (`IEcbGateway`, `ICurrencyRatesCache`, ...) implemented elsewhere. Keep it that way: put a new Redis or HTTP implementation in `App.Host/Infrastructure` or a gateway project, not in `Core.Service`.
- `Wallet.Api` references only `Core.Service`. `App.Host` references everything and wires it up.
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
| `POST /api/wallets/{walletId}/adjustbalance?amount=&currency=&strategy=` + `Idempotency-Key` header | `wallet-adjust` | `Idempotent-Replayed: true` on replay |

Error mapping in [WalletController.cs](src/Apis/Wallet.Api/Controllers/WalletController.cs): `KeyNotFoundException` -> 404, `ConcurrencyConflictException` -> 409, `IdempotencyKeyReuseException` -> 422, **any other exception -> 400** with `{ "error": message }`. Rate limited -> 429 with `Retry-After`. Note that `InsufficientFundsException` and validation `ArgumentException`s currently fall into the generic 400.

## Domain invariants (don't break these)

**Money and currencies**
- Always `decimal`, never `double`. Balances are `decimal(18,4)`, rates `decimal(18,6)`. Converted balances are rounded to 4 places.
- Currency codes are 3-letter ISO, stored upper-case as `char(3)` (non-Unicode). Compare case-insensitively.
- All rates are **relative to EUR**. EUR is always 1 (the gateway injects it, and `WalletService.GetEuroRate` short-circuits it). Conversion is `balance / rate(from) * rate(to)`.

**Entities** ([Core.Service/Entities](src/Core.Service/Entities))
- Private setters, a private parameterless constructor for EF, a static `Create(...)` factory that validates, and behaviour methods (`Credit`, `Debit`, `ForceDebit`, `UpdateRate`) that set `UpdatedAt`. Don't add public setters; add a method.
- Timestamps are `DateTime.UtcNow`.
- EF mapping lives in `IEntityTypeConfiguration<T>` classes under `Data/Configurations`, discovered automatically.

**Balance adjustments**
- Strategies ([Core.Service/Strategies](src/Core.Service/Strategies)) implement `IBalanceStrategy`, are selected by `Name` (case-insensitive) through `BalanceStrategyFactory`, and are registered as **singletons** in `Program.cs`. They must be stateless. Adding one: create the class, register it, and update the "Supported strategies" message in `BalanceStrategyFactory`, `tests/requests.sh` and the README.
- `SubtractFundsStrategy` rejects going negative; `ForceSubtractFundsStrategy` allows it on purpose.
- `AccountWallet.RowVersion` is an optimistic concurrency token. Concurrent updates surface as `DbUpdateConcurrencyException`, which becomes `ConcurrencyConflictException` (409). Don't add locks around it.

**Idempotency** ([WalletService.AdjustBalanceAsync](src/Core.Service/Services/WalletService.cs))
- `Idempotency-Key` is required, max 100 characters (`IdempotencyRecord.MaxKeyLength`).
- The `IdempotencyRecord` is saved in **the same `SaveChanges`** as the balance change, so both commit or neither does. Keep that when changing this code.
- The request hash is SHA-256 over normalised `walletId|amount|CURRENCY|strategy`. The same key with the same request replays the stored result; the same key with a different request -> 422.
- On `DbUpdateException` the change tracker is cleared and the key is looked up again, because a parallel request may have won.

## Infrastructure patterns

**Redis**: exactly one shared `IConnectionMultiplexer`, registered by `AddRedis` ([RedisServiceCollectionExtensions.cs](src/App.Host/Infrastructure/RedisServiceCollectionExtensions.cs)) with `AbortOnConnectFail = false` and `BacklogPolicy.FailFast`. Any new Redis feature must resolve this instance, not open its own connection.

**Fail open**: Redis is an optimisation, never a hard dependency. Rate limiting ([FailOpenRateLimiter.cs](src/App.Host/Infrastructure/RateLimiting/FailOpenRateLimiter.cs)) and the rates cache ([RedisCurrencyRatesCache.cs](src/App.Host/Infrastructure/Caching/RedisCurrencyRatesCache.cs)) catch `RedisException or RedisTimeoutException`, log a warning and carry on. New Redis code should do the same. The warning messages (`Rate limiter store is unavailable`, `Currency rates cache is unavailable`) are meant to be alerted on, so don't reword them casually.

**Redis keys**: version the key when the format changes (`currency-rates:latest:v1`), always set a TTL, and use `MULTI/EXEC` transactions for multi-step writes. Rate limit keys use hash tags for Redis Cluster; that is why route braces are stripped from the partition key.

**Currency rates**: `EcbSyncJob` runs every minute on **one node** (Quartz clustered SQL Server job store, `[DisallowConcurrentExecution]`). It upserts rates into SQL Server keyed by `(CurrencyCode, RateDate)`, then rebuilds the Redis snapshot from the database on every run, even when nothing changed. Reads go through `CurrencyRatesProvider`: Redis first; on a miss, the database, then `AddIfMissingAsync` (a `WATCH`-guarded fill that never overwrites a fresher snapshot). Don't add an in-process cache (`IMemoryCache`, `HybridCache`): other nodes would serve stale rates. The README explains why.

**Rate limiting**: fixed window per client IP per endpoint (HTTP method + route template), with counters in Redis. IPv6 is grouped by `/64`. Adding a policy:
1. Add a constant to [RateLimitPolicies.cs](src/Apis/Wallet.Api/RateLimitPolicies.cs) **and list it in `All`**.
2. Configure `RateLimiting:Policies:<name>` (`PermitLimit`, `Window` >= `00:00:01`) in `appsettings.json`.
3. Put `[EnableRateLimiting(RateLimitPolicies.X)]` on the endpoint.
4. Add limits for it in `RateLimitedApp` and update the README table.

**Client IP**: `X-Forwarded-For` is trusted only from `ForwardedHeaders:KnownProxies` / `KnownNetworks` (loopback by default). Never trust it more broadly, or clients can spoof their IP to get round rate limits.

**Configuration and options**
- Connection strings (`DefaultConnection`, `Redis`) come from environment variables (`ConnectionStrings__X`) and are read with `GetRequiredConnectionString`, which throws at startup if they are missing. Never commit real secrets.
- Options classes: `sealed`, a `SectionName` const, `init` properties with **no defaults** (missing config should fail), an `internal bool IsValid()`, registered with `.Bind(...).Validate(...).ValidateOnStart()` inside an `IServiceCollection` extension method (`AddXxx`) in `App.Host/Infrastructure`. Add a case to [StartupValidationTests.cs](tests/Integration.Tests/StartupValidationTests.cs).

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

- **Unit tests** (`tests/Unit.Tests`): xUnit + NSubstitute + FluentAssertions, for `Core.Service` logic. Mock interfaces, not `DbContext`.
- **Integration tests** (`tests/Integration.Tests`): join `[Collection(RedisCollection.Name)]` to share the Testcontainers Redis ([RedisFixture.cs](tests/Integration.Tests/RedisFixture.cs)). Tests in that collection run one at a time, so isolate state: rate limit tests take a fresh IP from `RateLimitedApp.NextClientIp()`, and cache tests reset their key first.
- Integration tests register the **production** extension methods (`AddRedis`, `AddClientIpRateLimiting`, ...) instead of rebuilding them, so the production code path is what gets tested. There is no SQL Server in tests; the wallet handler is substituted.
- New behaviour needs tests at the matching level, including the Redis-down (fail-open) path for anything that touches Redis.

## Documentation

The README is the design record and is kept up to date with every feature: how it works, configuration, trade-offs and production considerations. When you add or change behaviour, config keys, endpoints or policies, update the README (and `tests/requests.sh` / `tests/rate-limit.sh` if requests change) in the same change. Update this file when a convention or invariant changes.

## Git workflow

- Branch from `master` as `feature/<name>` or `chore/<name>`; PRs go to `master`.
- Commit messages: `feature: <Sentence.>` or `chore: <Sentence.>` (e.g. `feature: Add rate limit mechanism with Redis.`).
- Before opening a PR, run `dotnet build` (0 warnings) and `dotnet test`.
