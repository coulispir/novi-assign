# novi-assign

A wallet management API built with ASP.NET Core on .NET 10. Wallets live in SQL Server. A Quartz job pulls exchange rates from the European Central Bank (ECB), Redis caches the latest rates, and Redis also backs per-client rate limiting.

## Assignment coverage

Where each task of the NoviCode assignment is implemented and tested:

| Requirement | Implementation | Tests |
|---|---|---|
| **Task 1**: gateway library, ECB feed → strongly typed objects | Standalone `Ecb.Gateway` project: [EcbClient](src/Ecb.Gateway/EcbClient.cs) → `EcbDailyRates`, adapted to the core by [EcbGatewayAdapter](src/App.Host/Infrastructure/Ecb/EcbGatewayAdapter.cs) ([details](#ecb-rate-sync)) | [EcbClientTests](tests/Integration.Tests/Gateways/EcbClientTests.cs), `EcbGatewayAdapterTests` |
| **Task 2**: job every minute, one rate per currency per date (update or insert) | Quartz [EcbSyncJob](src/Core.Service/Jobs/EcbSyncJob.cs), clustered so it runs on one node; interval from `EcbSync:Interval` | [EcbRatesServiceTests](tests/Unit.Tests/Application/EcbRatesServiceTests.cs), `EcbSyncJobTests`, [EcbConfigurationTests](tests/Integration.Tests/Gateways/EcbConfigurationTests.cs) |
| **Task 2**: persist with raw SQL `MERGE`, all changes in one transaction | One parameterised `MERGE INTO CurrencyValues WITH (HOLDLOCK)` in [CurrencyValueRepository](src/Core.Service/Repositories/CurrencyValueRepository.cs) | [CurrencyRatesMergeTests](tests/Functional.Tests/CurrencyRatesMergeTests.cs) (real SQL Server) |
| **Task 3**: wallet `Id` (long), `Balance` (decimal), `Currency` (string) | [AccountWallet](src/Core.Service/Entities/AccountWallet.cs) | `AccountWalletTests` |
| **Task 3**: create wallet; `GET /api/wallets/{walletId}?currency=`; `POST /api/wallets/{walletId}/adjustbalance?amount=&currency=&strategy=` | [WalletController](src/Apis/Wallet.Api/Controllers/WalletController.cs), exactly these routes | [WalletLifecycleTests](tests/Functional.Tests/WalletLifecycleTests.cs) |
| **Task 3**: positive amount; `AddFundsStrategy`, `SubtractFundsStrategy` (throws on insufficient funds), `ForceSubtractFundsStrategy` | Strategy pattern, resolved by [BalanceStrategyFactory](src/Core.Service/Strategies/BalanceStrategyFactory.cs) from the `BalanceStrategyType` enum | [BalanceStrategyTests](tests/Unit.Tests/Domain/BalanceStrategyTests.cs), `AdjustBalanceHandlerTests` |
| **Task 3**: conversion between the wallet's currency and the requested one | [CurrencyConverter](src/Core.Service/Services/CurrencyConverter.cs), for balance display and for [adjustments in another currency](#adjustments-in-another-currency) | `CurrencyConverterTests`, [CurrencyConversionTests](tests/Functional.Tests/CurrencyConversionTests.cs) |
| **Bonus 1**: rates cache, refreshed by every job run | Redis snapshot ([RedisCurrencyRatesCache](src/App.Host/Infrastructure/Caching/RedisCurrencyRatesCache.cs)), read through [CurrencyRatesProvider](src/Core.Service/Services/CurrencyRatesProvider.cs) ([details](#currency-rates-cache)) | [RedisCurrencyRatesCacheTests](tests/Integration.Tests/Caching/RedisCurrencyRatesCacheTests.cs), `CurrencyRatesProviderTests` |
| **Bonus 2**: per-client-IP rate limit per endpoint | ASP.NET Core rate limiting with Redis-backed fixed windows ([details](#rate-limiting)) | [RateLimitingTests](tests/Integration.Tests/RateLimiting/RateLimitingTests.cs) |
| **Tech stack**: .NET 5+, Entity Framework, Options pattern, Quartz, xUnit | .NET 10, EF Core + SQL Server, validated options (e.g. [AddEcbGateway](src/App.Host/Infrastructure/Ecb/EcbServiceCollectionExtensions.cs)), Quartz, xUnit + NSubstitute + FluentAssertions | `EcbConfigurationTests`, `StartupValidationTests` |
| **Patterns**: interfaces + implementations, Decorator, Factory | Ports in `Core.Service/Interfaces`; Decorators [LoggingEcbGatewayDecorator](src/Core.Service/Decorators/LoggingEcbGatewayDecorator.cs) and [FailOpenRateLimiter](src/App.Host/Infrastructure/RateLimiting/FailOpenRateLimiter.cs); Factory `BalanceStrategyFactory`; Strategy; Adapter | [LoggingEcbGatewayDecoratorTests](tests/Unit.Tests/Application/LoggingEcbGatewayDecoratorTests.cs), `BalanceStrategyTests` |

On top of what the brief asked for, there are [health checks](#health-checks), optional [idempotent adjustments](#idempotent-adjustments-optional), optimistic concurrency, one [error format](#errors) for every failure, [OpenAPI and Swagger UI](#api-documentation), a [load-balanced setup](#load-balanced-setup), around 200 tests and [CI](#ci).

## Architecture

```
src/
  Apis/Wallet.Api     Controllers, request/response models, error mapping (references Core.Service only)
  Core.Service        Domain and application logic: entities, one handler per use case, strategies,
                      services, EF Core + migrations, the Quartz job, and the interfaces (ports)
                      for everything external
  Ecb.Gateway         Standalone ECB feed client (references no other project)
  App.Host            Composition root: Program.cs, DI, middleware, configuration, and the adapters
                      that implement the core's ports (Redis cache, rate limiting, ECB adapter)
tests/
  Unit.Tests          Domain and application logic, no infrastructure
  Integration.Tests   Adapters against real Redis (Testcontainers), HTTP pipeline in memory
  Functional.Tests    The real Program.cs against real SQL Server and Redis containers
```

Each use case has its own handler in [Core.Service/Handlers](src/Core.Service/Handlers). `CreateWalletHandler` and `AdjustBalanceHandler` are commands and change state. `GetBalanceHandler` is a query: it only reads, and loads the wallet without EF change tracking. Every handler checks its own input and returns a plain result record rather than an entity. The controller turns that into a response model from `Wallet.Api/Models`, so changing something in the core can't quietly change the HTTP contract.

Dependencies point inward. `Core.Service` doesn't know about ASP.NET, Redis or HTTP clients, and `Ecb.Gateway` knows nothing about this application. The replicas themselves hold no state: wallets, rates and the Quartz cluster live in SQL Server, and the rates snapshot and rate limit counters live in Redis.

## Running locally

You need Docker (Desktop, or Engine with Compose v2) to run the app. To run the tests you also need the [.NET 10 SDK](https://dotnet.microsoft.com/download), with Docker running for the integration and functional tests.

```bash
cp .env.sample .env
docker compose up -d
```

This starts SQL Server, Redis and the API on `http://localhost:${APP_PORT}` (`5000` by default). [tests/requests.sh](tests/requests.sh) has example requests.

This default setup is meant for development. It runs the API from source with `dotnet watch`, so code changes reload straight away, and it connects to SQL Server as `sa`. Data is kept in the `mssql-data` volume, and `docker compose down -v` wipes it. [Production considerations](#production-considerations) covers what a real deployment does differently.

### API documentation

In Development the API publishes an [OpenAPI](https://www.openapis.org/) 3.0 document, plus Swagger UI for browsing and trying it:

- Swagger UI: `http://localhost:5000/swagger`
- OpenAPI document: `http://localhost:5000/openapi/v1.json`. You can share it or generate clients from it.

Nothing is written by hand. ASP.NET Core's built-in `Microsoft.AspNetCore.OpenApi` generates the document at runtime from the controllers' routes, parameters and `[ProducesResponseType]` attributes, and Swagger UI (`Swashbuckle.AspNetCore.SwaggerUI`) just displays it. Both are set up in [ApiDocumentationExtensions.cs](src/App.Host/Infrastructure/ApiDocumentationExtensions.cs).

A few details worth knowing:

- The document is OpenAPI 3.0 rather than .NET 10's default of 3.1, because client generators (openapi-generator for Kotlin, for example) handle 3.0 more reliably.
- Numbers are documented as plain numbers. By default .NET describes every number as "number or string", since its JSON settings also accept numbers sent as strings. Swagger UI can't fill in a parameter like that (it rejects every value with `amount: Required field is not provided`), and generated clients would treat amounts as strings. A schema transformer keeps only the number type. The API itself still accepts both.
- It's only published in Development. Production doesn't advertise its API, so the [load-balanced setup](#load-balanced-setup), which runs as `Production`, has no `/swagger`.
- `strategy` shows up as a dropdown. It's the [BalanceStrategyType](src/Core.Service/Strategies/BalanceStrategyType.cs) enum, so the document lists its three names and generated clients get a typed enum. Names are matched ignoring case (`addfundsstrategy` works). Anything else gets `400 invalid_request`, including numbers like `strategy=0`, which ASP.NET would otherwise silently map to the first strategy.
- Every status code an action can return has to be declared with `[ProducesResponseType]`, or it won't appear in the contract. [OpenApiDocumentTests](tests/Integration.Tests/OpenApi/OpenApiDocumentTests.cs) checks the endpoints, the `Idempotency-Key` header, every status code of `adjustbalance`, and the `ErrorResponse` and `BalanceResponse` schemas.

### Load-balanced setup

To run the app the way it would run in production, add [docker-compose.lb.yml](docker-compose.lb.yml) on top of the default setup:

```bash
docker compose -f docker-compose.yml -f docker-compose.lb.yml up -d --build
```

```
client ──► nginx :5000 ──round-robin──► webapi replica 1 ─┐
                                    └─► webapi replica 2 ─┴─► SQL Server + Redis
```

- [nginx](deploy/nginx/nginx.conf) is the only way in, on `${APP_PORT}`. It spreads requests across the replicas, sets `X-Forwarded-For` to the connecting address (throwing away anything the client sent), and adds an `X-Upstream` response header so you can see which replica answered.
- The two API replicas run the production image from the [Dockerfile](Dockerfile): just the ASP.NET runtime, running as a non-root user, in the `Production` environment. They only trust `X-Forwarded-For` from nginx's fixed address, `172.28.0.10`.
- SQL Server and Redis, and their data, are shared with the default setup.

Send 12 requests and you'll see them alternate between replicas, while the `wallet-read` limit (10 per second) still applies to you as one client:

```bash
for i in $(seq 12); do curl -s -o /dev/null -w "%{http_code} via %header{x-upstream}\n" http://localhost:5000/api/wallets/1; done
```

The ECB sync job still runs on only one replica per trigger, because Quartz uses a clustered job store.

Some things to be aware of:

- Locally, every request looks like it comes from the same client. Docker Desktop hands nginx connections from its gateway (e.g. `172.28.0.1`) rather than your machine's real IP. That's enough to show the limit holding across replicas, and the integration tests cover different clients and faked headers.
- The image doesn't hot reload, since it's built once. Rebuild with `--build` after changing code.
- To go back to the hot-reload setup, run `docker compose up -d --remove-orphans`.

## Health checks

| Endpoint | Checks | Use it for |
|---|---|---|
| `GET /health` | SQL Server (`SELECT 1`) and Redis (`PING`) | The load balancer and readiness probes: should this replica get traffic? |
| `GET /health/live` | Nothing beyond the process answering | Liveness probes: should this replica be restarted? |

```json
{ "status": "Healthy", "totalDurationMs": 9, "checks": { "sql-server": { "status": "Healthy", "durationMs": 5 }, "redis": { "status": "Healthy", "durationMs": 7 } } }
```

If SQL Server is down, `/health` reports `Unhealthy` with a `503`. The app can't serve wallets without the database, so the load balancer should stop sending it traffic.

If Redis is down, it reports `Degraded` but still returns `200`. Rate limiting and the rates cache [keep working without Redis](#rate-limiting), so the app is still usable. Reporting Unhealthy would make the load balancer pull every replica at once over something the app can live without. The status still shows the outage.

The liveness endpoint never checks dependencies, so a database outage doesn't make the orchestrator restart perfectly healthy processes over and over.

Each check has a timeout (`HealthChecks:Timeout`, 5 seconds by default, which should stay below the probe's own timeout). The SQL check opens its own connection instead of going through EF Core, because EF's retry logic would hold a probe for several seconds during an outage.

The endpoints are safe to expose. The response only contains statuses and durations, never error messages or connection details, and failures are logged instead. Responses are never cached, and successful probes are left out of the request log so they don't flood it.

The code is in [DependencyHealthCheckExtensions](src/App.Host/Infrastructure/HealthChecks/DependencyHealthCheckExtensions.cs). `DependencyHealthCheckTests` covers outages against real Redis, and the functional `HealthCheckTests` run against the real app.

## Errors

Every error the API returns has the same JSON body:

```json
{ "error": "Wallet lacks sufficient funds to complete this operation.", "code": "insufficient_funds" }
```

`code` never changes, so that's what clients should check. `error` is a message for people to read and may be reworded.

| Status | `code` | When |
|---|---|---|
| 400 | `invalid_request` | Invalid input: a missing or unparsable parameter, malformed JSON, an unknown strategy, non-positive amount, a blank or too long `Idempotency-Key`, bad currency code, negative initial balance, an amount in another currency that converts to less than 0.0001 of the wallet's currency |
| 400 | `unsupported_currency` | No exchange rate is known for the requested conversion or adjustment currency |
| 404 | `wallet_not_found` | The wallet doesn't exist |
| 409 | `concurrency_conflict` | Another request changed the wallet at the same time. Nothing was applied, so retry the request (with the same `Idempotency-Key`, if you sent one) |
| 422 | `insufficient_funds` | `SubtractFundsStrategy` would take the balance below zero |
| 422 | `idempotency_key_reused` | The `Idempotency-Key` was already used for a different request |
| 429 | `rate_limited` | Rate limit exceeded; see [Rate limiting](#rate-limiting) |
| 500 | `internal_error` | Anything unexpected. The details are logged, never returned |

### How it works

Exceptions are turned into responses in one place. [ApiExceptionFilter](src/Apis/Wallet.Api/Filters/ApiExceptionFilter.cs) is applied to `WalletController`, so the actions only deal with the success case. `Core.Service` throws its own exceptions ([Core.Service/Exceptions](src/Core.Service/Exceptions)) and knows nothing about HTTP status codes.

Only expected failures become 4xx responses. Anything not in the table, like a SQL Server outage or a bug, is a `500` with a generic message. That way clients aren't told it's their fault, monitoring sees a server error, and internal details such as SQL error text never end up in a response. The exception is logged as `Unhandled exception while processing {Method} {Path}`.

Some exceptions happen outside the controllers (in middleware, routing or while writing the response) and never reach the filter. For those, `UseExceptionHandler()` is the outermost middleware and tries two handlers in order. [HttpExceptionHandler](src/App.Host/Infrastructure/Errors/HttpExceptionHandler.cs) keeps the original `4xx` (as `invalid_request`) when the server itself rejected the request, for example a body over the size limit. [GenericExceptionHandler](src/App.Host/Infrastructure/Errors/GenericExceptionHandler.cs) turns everything else into a logged, generic `500`. Both return the same body. In Development you get ASP.NET Core's detailed error page instead.

Client mistakes have their own exception type, `DomainValidationException`. `ArgumentException` is kept for guard clauses that catch programming errors, and those are `500`s.

If the client disconnects, the request was cancelled rather than failed, so the filter doesn't log an error or write a response.

Requests that ASP.NET Core rejects before the action runs (a missing required parameter, a value it can't parse, broken JSON) use the same body too. `AddWalletApi` ([WalletApiServiceCollectionExtensions.cs](src/Apis/Wallet.Api/WalletApiServiceCollectionExtensions.cs)) swaps the default problem details for an `ErrorResponse` with `invalid_request`, and puts the parameter name in front of each message (e.g. `strategy: The value 'Transfer' is not valid.`). Clients only ever have to handle one error format.

To add a new error, create an exception in `Core.Service/Exceptions` and throw it from the domain code, add a code to [ErrorCodes](src/Apis/Wallet.Api/Models/ErrorResponse.cs), map it in `ApiExceptionFilter`, add a case to [ErrorResponseTests](tests/Integration.Tests/ErrorHandling/ErrorResponseTests.cs), and cover it end to end in [Functional.Tests](tests/Functional.Tests).

## Rate limiting

Each client IP gets a limited number of requests per time window on each endpoint. Once over the limit, requests get `429 Too Many Requests` with a `Retry-After` header (in seconds) and this body:

```json
{ "error": "Too many requests. Please retry later.", "code": "rate_limited" }
```

To try every limit against a running stack, run `tests/rate-limit.sh`. You can pass it a base URL; the default is `http://localhost:5000`.

### How it works

It uses ASP.NET Core's built-in rate limiting middleware (`AddRateLimiter` and `[EnableRateLimiting]`), with the counters kept in Redis through [RedisRateLimiting](https://github.com/cristipufu/aspnetcore-redis-rate-limiting). With in-memory counters, each node behind the load balancer would give every client its own separate allowance. Keeping them in Redis means the limit holds across all nodes.

The algorithm is a fixed window. A window starts with the client's first request and resets when it expires. It needs very little memory per client and gives an exact `Retry-After`. The downside is that a client can squeeze in up to twice the limit in a short burst around the moment one window ends and the next begins.

Concurrent requests can't slip past the limit, even on different nodes, because each check-and-increment runs as one atomic Lua script inside Redis.

Budgets are per client IP and per endpoint. An endpoint is its HTTP method plus route template, so `/api/wallets/1` and `/api/wallets/2` share a budget. IPv6 clients are grouped by `/64` block, since one subscriber usually owns a whole block and could otherwise just rotate addresses.

If Redis can't be reached, requests are let through and a warning is logged, rather than every request failing with a 500. While disconnected, Redis commands fail immediately, so an outage doesn't slow requests down.

### Policies

Each endpoint picks a named policy with `[EnableRateLimiting]`. The names are in [RateLimitPolicies.cs](src/Apis/Wallet.Api/RateLimitPolicies.cs) and the limits in `appsettings.json`:

| Policy | Endpoint | Default limit |
|---|---|---|
| `wallet-read` | `GET /api/wallets/{walletId}` | 10 per second |
| `wallet-create` | `POST /api/wallets` | 5 per minute |
| `wallet-adjust` | `POST /api/wallets/{walletId}/adjustbalance` | 30 per minute |

```json
"RateLimiting": {
  "Policies": {
    "wallet-read": { "PermitLimit": 10, "Window": "00:00:01" },
    "wallet-create": { "PermitLimit": 5, "Window": "00:01:00" },
    "wallet-adjust": { "PermitLimit": 30, "Window": "00:01:00" }
  }
}
```

`Window` is a `TimeSpan` with a minimum of one second (`00:00:01`). Redis tracks windows in whole seconds, so a window can run up to a second longer than configured. That makes the limit slightly stricter, never looser. The app won't start if any policy listed in `RateLimitPolicies.All` is missing or has invalid settings.

To add a policy, add a constant to `RateLimitPolicies` and list it in `All`, configure it under `RateLimiting:Policies`, and put `[EnableRateLimiting(RateLimitPolicies.YourPolicy)]` on the endpoint.

### Client IP behind a load balancer

The client's real IP is read from `X-Forwarded-For`, but only when the request comes from a proxy the app trusts. If it trusted the header from anyone, a client could fake it and dodge the limit. By default only loopback is trusted, so list your load balancer's addresses before deploying behind one:

```json
"ForwardedHeaders": {
  "ForwardLimit": 1,
  "KnownProxies": [ "10.0.0.5" ],
  "KnownNetworks": [ "10.0.0.0/16" ]
}
```

`KnownProxies` takes single addresses and `KnownNetworks` takes CIDR ranges. `ForwardLimit` is the number of proxy hops in front of the app. Until the load balancer is listed, the app sees every request as coming from the load balancer, so all clients share one budget. The app won't start if an entry isn't a valid address or CIDR range, or if `ForwardLimit` is below 1.

### Redis

The connection string is `ConnectionStrings:Redis`, which Docker Compose sets to `redis:6379`. A single shared `IConnectionMultiplexer` is registered in [RedisServiceCollectionExtensions.cs](src/App.Host/Infrastructure/RedisServiceCollectionExtensions.cs). Rate limiting and the [currency rates cache](#currency-rates-cache) both use it, and any new Redis feature should use it too instead of opening its own connection.

## ECB rate sync

[EcbSyncJob](src/Core.Service/Jobs/EcbSyncJob.cs) runs at startup and then every minute by default, on one node per trigger. It fetches the ECB daily feed through the [Ecb.Gateway](src/Ecb.Gateway/EcbClient.cs) library, saves the rates to SQL Server, and then refreshes the [currency rates cache](#currency-rates-cache).

```
ECB feed ──► EcbClient (Ecb.Gateway: EcbDailyRates) ──► EcbGatewayAdapter (+ EUR, EcbRateResult) ──► LoggingEcbGatewayDecorator ──► EcbRatesService (validate, de-duplicate) ──► one MERGE ──► CurrencyValues
```

The gateway is a standalone library that references no other project. It exposes `IEcbClient`, which returns the feed as typed objects (`EcbDailyRates`, holding the publication date and each `EcbRate`), so any application could reuse it. The core doesn't depend on it either. `Core.Service` defines the `IEcbGateway` interface it needs, and [EcbGatewayAdapter](src/App.Host/Infrastructure/Ecb/EcbGatewayAdapter.cs) in the host connects the two. The adapter also adds EUR with a rate of 1: every ECB rate is quoted against the euro, and the feed doesn't list EUR itself.

`CurrencyValues` keeps one row per currency per date, so it holds the full rate history. A new day adds rows, and the same day again updates them.

### How it works

Each sync is a single raw SQL `MERGE`. [CurrencyValueRepository.MergeRatesAsync](src/Core.Service/Repositories/CurrencyValueRepository.cs) sends the whole feed in one `MERGE INTO CurrencyValues` statement, matching on the unique `(CurrencyCode, RateDate)` index:

- a currency with no row for that date is inserted;
- a row whose rate changed is updated (the rate and `UpdatedAt`);
- a row whose rate is the same doesn't match any `WHEN` clause, so it isn't rewritten.

One statement means one round trip and one transaction, so either all rates are saved or none are. `OUTPUT $action` reports what happened to each row, which gives the `Inserted` and `Updated` counts in the job's log line.

Values only ever go in as parameters. The SQL text contains nothing but generated parameter names (`@c0, @r0, @d0, @u0, ...`), and each value is sent as a typed parameter matching its column (`char(3)`, `decimal(18,6)`, `date`, `datetime2`). Feed data can't change the statement, and no precision is lost.

`WITH (HOLDLOCK)` keeps the matched key range locked until the insert happens. Without it, two merges running at once (say, a manual run during a scheduled one) could both try to insert the same currency and date, and one would fail on the unique index.

The feed is cleaned up before the merge. `EcbRatesService` validates every rate through `CurrencyValue.Create` (a 3-letter code, a positive rate, a date with no time) and keeps one entry per currency and date, because a `MERGE` fails if two source rows match the same target row. One invalid rate fails the whole sync before anything is written, and the job tries again on its next run.

SQL Server allows 2100 parameters per statement and each rate uses 4, so one merge can take at most 500 rates (`MaxRatesPerMerge`). The daily feed has about 30.

Running the same merge twice changes nothing, so it's safe for EF's retry on transient SQL errors to repeat it.

Every call to the feed is timed and logged by a decorator. [LoggingEcbGatewayDecorator](src/Core.Service/Decorators/LoggingEcbGatewayDecorator.cs) wraps `IEcbGateway` and logs something like `Fetched 30 rates from the ECB feed for 2026-09-29 in 231 ms`, or a warning with the elapsed time and the exception if the call fails. It passes results and exceptions through untouched, so neither the adapter nor the job knows it's there. It's wired up with the built-in container: `AddEcbGateway` registers the adapter as itself, and `IEcbGateway` resolves to the decorator wrapped around it. `FailOpenRateLimiter` wraps the Redis rate limiter the same way.

### Configuration

The feed URL, the HTTP timeout and the job interval come from `appsettings.json` through the Options pattern:

```json
"Ecb": {
  "DailyRatesUrl": "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml",
  "Timeout": "00:00:15"
},
"EcbSync": {
  "Interval": "00:01:00"
}
```

`Ecb` binds to the gateway library's `EcbClientOptions`, registered by [AddEcbGateway](src/App.Host/Infrastructure/Ecb/EcbServiceCollectionExtensions.cs). `Timeout` applies to each request to the feed.

`EcbSync:Interval` sets the Quartz trigger, registered by [AddEcbSyncJob](src/App.Host/Infrastructure/Jobs/EcbSyncServiceCollectionExtensions.cs). The trigger lives in the clustered job store and Quartz overwrites it on startup, so a new interval takes effect once the nodes restart. Keep `CurrencyRatesCache:TimeToLive` longer than the interval.

Like any setting, these can be overridden per environment, e.g. `EcbSync__Interval=00:05:00`. The app won't start if the URL isn't an absolute http(s) URL, or if the timeout or interval is missing or not positive.

## Idempotent adjustments (optional)

`POST /api/wallets/{walletId}/adjustbalance?amount=&currency=&strategy=` works exactly as the assignment describes, with no extra header. Every request applies its adjustment, so sending the same request twice applies it twice, like any plain `POST`.

Retries can be made safe by sending an `Idempotency-Key` header with a unique value such as a UUID. That's useful after a timeout, when the client can't tell whether the first attempt went through:

```bash
curl -sS -i -X POST 'http://localhost:5000/api/wallets/1/adjustbalance?amount=5&currency=EUR&strategy=AddFundsStrategy' -H "Idempotency-Key: $(uuidgen)"
```

The first request with a key applies the adjustment and stores the result, in the same transaction as the balance change. A retry with the same key and the same parameters isn't applied again. It gets the stored result back with an `Idempotent-Replayed: true` header, even if several retries arrive at the same time. Using the same key with different parameters is rejected with `422 idempotency_key_reused`.

A key that is sent has to be 1-100 characters and not blank, otherwise the request gets `400 invalid_request`. An empty header counts as no key.

Concurrent adjustments are safe without a key too. The wallet's row version makes a conflicting update fail with `409 concurrency_conflict` instead of silently losing an update.

## Adjustments in another currency

An adjustment can be made in any currency with a known exchange rate, not just the wallet's own. For example, `amount=50&currency=USD` on a EUR wallet converts the 50 USD to EUR and then applies the strategy:

```bash
curl -sS -X POST 'http://localhost:5000/api/wallets/1/adjustbalance?amount=50&currency=USD&strategy=AddFundsStrategy'
```

The amount is converted at the latest ECB rates, through EUR (`amount / rate(from) * rate(to)`), and rounded to 4 decimal places, which is the precision balances are stored with. The balance conversion on `GET` uses the same [CurrencyConverter](src/Core.Service/Services/CurrencyConverter.cs), so the two can never disagree. Rates come from the [currency rates cache](#currency-rates-cache).

Balance rules apply to the converted amount. `SubtractFundsStrategy` compares it with the balance, so it refuses anything that would take the wallet below zero in its own currency (`422 insufficient_funds`). The response always shows the wallet's new balance in the wallet's currency.

When something goes wrong the balance stays as it was. A currency without a rate gets `400 unsupported_currency`, and an amount that converts to less than 0.0001 of the wallet's currency gets `400 invalid_request`.

A retry with the same `Idempotency-Key` returns the balance from the first attempt, even if the rates have changed since. It's never converted a second time.

## Currency rates cache

Currency conversion (`GET /api/wallets/{walletId}?currency=USD`, and adjustments in another currency) reads exchange rates from Redis, not SQL Server. The database is only queried when the cache is empty or can't be reached.

```
EcbSyncJob (every EcbSync:Interval, one node) ──► SQL Server ──► latest rate per currency ──► Redis (replace snapshot)
GET /api/wallets/{id}?currency=X ──► Redis ──hit──► convert
                                        └──miss──► SQL Server ──► fill Redis if still empty ──► convert
```

### How it works

The rates are one shared snapshot in Redis rather than an in-process cache. The sync job only runs on one node per trigger, so an in-memory cache would get refreshed on that node and go stale everywhere else. With Redis, every node sees the new rates as soon as the job finishes.

The snapshot is a single hash at `currency-rates:latest:v1`, mapping each currency code to its latest rate against EUR. Reading it is one `HGETALL` of about 30 small fields. The `v1` in the key means a future format change can be rolled out without old and new nodes misreading each other's data.

The job refreshes the cache on every run, straight after saving to SQL Server, even if no rate changed. The snapshot is rebuilt from the database (the latest rate per currency) rather than from the ECB response, so the cache always matches the database. It's swapped in one `MULTI/EXEC` transaction, so readers never see a half-written snapshot, and currencies that are no longer in the database drop out. Since every run rewrites it, the cache recovers within one job interval after a Redis restart, eviction or outage.

On a cache miss (for example at first startup, before the job has run), the request reads the rates from SQL Server and writes them to Redis only if the key still doesn't exist, using a `WATCH`-based transaction. If the job writes a fresher snapshot while the request is still reading the database, the request's older data is thrown away instead of overwriting the new one.

A currency that isn't in the snapshot has no rate, since the snapshot holds every currency. The request fails with `400 unsupported_currency` without touching the database.

If Redis can't be reached, requests read the rates from SQL Server and log a warning (`Currency rates cache is unavailable`) instead of failing. As with rate limiting, Redis commands fail immediately while disconnected, so an outage doesn't add latency.

### Configuration

```json
"CurrencyRatesCache": {
  "TimeToLive": "01:00:00"
}
```

`TimeToLive` is just a safety net, because the job rewrites the snapshot on every run (every minute by default). It stops the key from living forever and lets Redis evict it under a `volatile-*` `maxmemory` policy. Keep it longer than the job interval, or requests will fall back to the database between runs. The app won't start if it's missing or not positive.

### Code

- [ICurrencyRatesProvider](src/Core.Service/Interfaces/ICurrencyRatesProvider.cs) / [CurrencyRatesProvider](src/Core.Service/Services/CurrencyRatesProvider.cs): the read path (cache first, then the database) used by `GetBalanceHandler` and `AdjustBalanceHandler`, and the refresh used by `EcbSyncJob`.
- [ICurrencyRatesCache](src/Core.Service/Interfaces/ICurrencyRatesCache.cs): the cache interface, which keeps Redis out of `Core.Service`.
- [RedisCurrencyRatesCache](src/App.Host/Infrastructure/Caching/RedisCurrencyRatesCache.cs): the Redis implementation, registered by `AddCurrencyRatesCache`.

## Tests

```bash
dotnet test                                # everything; Docker must be running
dotnet test tests/Unit.Tests               # unit tests only: no Docker, well under a second
```

### CI

[GitHub Actions](.github/workflows/ci.yml) runs on every pull request to `master` and every push to `master`, with two jobs side by side:

- **Build and test** builds in Release, where warnings are errors, so the analysers and code style are enforced as well. Then it runs all three test projects, with Testcontainers starting Redis and SQL Server on the runner's Docker. The run page shows a coverage summary (line and branch coverage per assembly, leaving out test projects and EF migrations). It's for information only; there's no minimum. If tests fail, the TRX results and coverage files are attached to the run as the `test-results` artifact.
- **Docker image** builds the production [Dockerfile](Dockerfile) without pushing it, so a broken image (a new project missing from the restore layer, say) fails the PR.

A new push to the same PR cancels the run it replaces. To make the checks mandatory, require both jobs in a branch protection rule for `master`.

The tests follow the layers of the code, and each layer is tested with the lightest setup that can still catch its bugs:

| Project | What it tests | Real dependencies |
|---|---|---|
| [Unit.Tests/Domain](tests/Unit.Tests/Domain) | Entities and balance strategies: validation, balance rules | none, and no mocks |
| [Unit.Tests/Application](tests/Unit.Tests/Application) | Handlers, services and sync job: coordination and business logic | none; ports mocked with NSubstitute |
| [Integration.Tests](tests/Integration.Tests) | Infrastructure adapters: Redis cache, rate limiting, ECB feed parsing, startup validation | Redis ([Testcontainers](https://dotnet.testcontainers.org/)); HTTP stubbed |
| [Functional.Tests](tests/Functional.Tests) | The API end to end, through `WebApplicationFactory<Program>` | SQL Server and Redis containers; only the ECB feed is faked |

### Unit tests

The domain tests cover wallet creation and the credit, debit and force-debit rules, currency rate validation, each balance strategy and the strategy lookup, including which exception each rule throws (see [Errors](#errors)).

The application tests cover:

- what the ECB sync passes to the merge (the whole feed in one call, normalised and de-duplicated, with invalid rates rejected and an empty feed skipped);
- conversion maths through EUR in `CurrencyConverter`, including rounding;
- each handler's input validation, and every strategy applied by `AdjustBalanceHandler` through the real factory;
- reading rates from the cache with a database fallback;
- the job refreshing the cache only after a successful sync.

Test names follow `Method_Scenario_Result`.

### Integration tests

- **Rate limiting** hosts the real `WalletController` with the production rate limiting setup and mocked handlers. It covers the 429 response, per-endpoint limits, budgets shared across route values and across two app nodes, 50 concurrent requests not exceeding the limit, trusted and spoofed `X-Forwarded-For`, IPv6 `/64` grouping, and carrying on when Redis is down.
- **Currency rates cache** runs the production cache setup against Redis: exact decimal round trips, atomic replacement, the conditional fill on a miss, the TTL, unreadable data, and carrying on when Redis is down.
- **ECB gateway** parses canned feed responses: every rate plus the EUR base, malformed entries, parsing that doesn't depend on culture, and error statuses.
- **Startup validation** checks that bad trusted-proxy settings and missing connection strings stop the host.
- **Error handling** hosts the real `WalletController` with handlers that throw. It checks that each domain exception gets its status code and `code`, and that unexpected exceptions (including a stray `ArgumentException`) return a generic `500` without leaking the message. No Redis or database needed.

### Functional tests

These start the real `Program.cs` (DI, middleware, migrations) against real SQL Server and Redis containers:

- **Wallet lifecycle:** create, read, each strategy, and every error path (400, 404, and 422 for insufficient funds), each checked against its [error code](#errors). Failed requests leave the balance unchanged.
- **ECB rate merge:** the raw SQL `MERGE` against SQL Server. It inserts missing dates, updates only rates that changed (unchanged rows keep their `UpdatedAt`), keeps the history per date, does nothing when repeated, keeps the full `decimal(18,6)` precision, and rejects more rates than one statement can carry.
- **Idempotency and concurrency:** replays, reusing a key for a different request (422), parallel retries with the same key applied exactly once, and parallel adjustments never losing an update.
- **Currency conversion:** the whole path from the ECB feed through the sync job, SQL Server and Redis to the endpoint. It also checks that rates are served from Redis rather than SQL Server, that an empty cache falls back to the database and refills, that new rates show up after a sync, and that a currency the ECB drops keeps its last rate. For adjustments in another currency, it checks credits and debits at the synced rate (including between two non-EUR currencies), the overdraft rule applied to the converted amount, and unknown currencies or amounts too small to convert being rejected without touching the balance.

A few choices keep these tests reliable:

- They use a real SQL Server, not EF Core's in-memory provider. Idempotency and lost-update protection rely on SQL Server enforcing the wallet's row version and the idempotency key's primary key. The in-memory provider enforces neither, so the tests would pass there even with the protections broken.
- The Quartz scheduler doesn't run. Tests call the real `EcbSyncJob` when they need a sync, instead of racing a background trigger.
- No test depends on another test's data. Each one creates its own wallets, and each test that changes rates owns one currency (see [FakeEcbFeed](tests/Functional.Tests/Infrastructure/FakeEcbFeed.cs)), so the database never needs resetting between tests.
- The tests keep their own copies of the response contracts, so renaming an API property breaks them just like it would break a real client.
- Test logs go to the test project's `bin/.../logs/`, not `src/App.Host/logs`.

## Design decisions and trade-offs

The main choices I made, and what each one costs:

- **Built for several replicas from day one.** All shared state is in SQL Server or Redis, never in process memory. The cost is a dependency on Redis, which the app can survive losing (see below).
- **A standalone gateway behind an interface.** `Ecb.Gateway` is a reusable library, the core defines the `IEcbGateway` interface it needs, and an adapter in the host joins them. It's one extra class, but either side can change or be replaced without touching the other.
- **Raw SQL `MERGE` for rates**, as the brief asks. It's one round trip and one transaction per sync, with `HOLDLOCK` against concurrent inserts and parameters only. Tracked EF entities would be simpler, but would send many statements.
- **A Redis snapshot instead of an in-process cache.** Every replica sees new rates as soon as the job finishes. The cost is a network hop per conversion, and a small in-process cache in front of Redis is the obvious next step if that ever matters.
- **Carry on when Redis is down.** Rate limiting and the cache step aside (no limits, rates read from the database) instead of failing requests. That favours staying up. For an endpoint where abuse is worse than downtime, it should fail closed instead.
- **Fixed-window rate limiting.** It uses very little memory and gives an exact `Retry-After`, but allows a burst of up to twice the limit around a window boundary. A sliding window would smooth that out at a higher cost per request.
- **Optimistic concurrency on wallets** (a SQL Server row version) instead of locks. Nothing blocks, and a conflicting update gets a `409` instead of silently losing money.
- **Optional idempotency.** The endpoint works exactly as specified, and an `Idempotency-Key` makes retries safe. The key is stored in the same transaction as the balance change.
- **One handler per use case, without a mediator.** Commands and queries have their own handlers and reads don't track entities, but there's no MediatR and no separate read model. With three endpoints, that would only add indirection. A separate read model (a projection, or a read replica) is where to go if reads ever need to scale separately from writes; see [Scaling](#scaling).
- **Strategies as an enum plus a factory.** The allowed values are part of the API contract (the Swagger dropdown, typed clients), and the factory checks at startup that every value has exactly one implementation.
- **One error format.** Every failure, framework validation included, returns `{ "error", "code" }` with a stable `code`, and unexpected errors never leak internals.
- **Tests at three levels, with real infrastructure where it counts.** Anything that depends on SQL Server behaviour (row versions, unique keys, `MERGE`) runs against a real SQL Server container, because EF's in-memory provider wouldn't enforce any of it.

## Scaling

The API already runs as several identical replicas behind a load balancer, so handling more traffic starts with adding replicas. That works up to a point, and then the pressure moves to the shared pieces behind them, mostly SQL Server. This section is what I'd look at next, roughly in the order I'd expect to need it. None of it is built. It's the plan, not the current state.

**Measure first.** Before changing anything, I'd run a load test (k6, for example) with a realistic mix of reads and adjustments, and watch SQL Server, Redis and the replicas. Guessing at bottlenecks usually means optimising the wrong thing. My expectation is that SQL Server hits its limit well before the app does.

**Busy wallets.** Wallets use optimistic concurrency: an adjustment reads the wallet, changes it, and saves it only if nobody else changed it in the meantime. That's cheap and safe while adjustments to the same wallet are spread out. If one wallet gets a lot of adjustments at once (a merchant's wallet, say), most of them will lose the race and get a `409`. There are a few ways to handle that, from least to most work:

1. Retry on the server. When a save loses the race, nothing was written, so `AdjustBalanceHandler` can reload the wallet and try again a couple of times with a small random delay before giving up with a `409`. Clients see far fewer conflicts, and nothing else changes.
2. Change the balance in one SQL statement, e.g. `UPDATE AccountWallets SET Balance = Balance + @amount WHERE Id = @id AND Balance >= @amount`. The database applies concurrent adjustments one after another, so there's no race to lose. The overdraft rule then has to live in the `WHERE` clause, and the idempotency record still has to be saved in the same transaction.
3. Record adjustments in a ledger. Adding a row per adjustment and calculating the balance from those rows means writes never fight over the same row. It's also what [Known limitations](#known-limitations-and-next-steps) suggests for auditing anyway, but it's the biggest change of the three.

**Move reads to a replica.** Reads and writes already go through separate handlers, so pointing `GetBalanceHandler` at a readable SQL Server secondary (a connection string with `ApplicationIntent=ReadOnly`) is a small change. That takes balance reads off the primary. The catch is that a replica can lag slightly, so a balance read straight after an adjustment might briefly show the old value. For a display endpoint that's usually acceptable, and anything that needs the exact latest balance can keep reading from the primary.

**Clean up idempotency records.** Every adjustment sent with an `Idempotency-Key` leaves a row in `IdempotencyRecords`, and nothing ever deletes them, so the table grows forever. Clients only retry for minutes or hours, so the records only need to be kept for a limited time, say 24 hours to a few days. A Quartz job, clustered like the ECB sync, could delete older rows in small batches. The table already has a `CreatedAt` column, but it would need an index on it. This one is worth doing even without a traffic increase.

**Watch the connection count.** Each replica keeps its own pool of database connections, so the total is the number of replicas times the pool size, and it has to stay under what SQL Server allows. That's easy to forget when scaling out. Switching to `AddDbContextPool` also reuses `DbContext` instances between requests instead of building a new one each time, which helps under load.

**Redis.** Every rate-limited request makes one round trip to Redis, so Redis load grows with traffic. A managed Redis cluster handles that, and the rate limit keys already use hash tags so they work with Redis Cluster. For the rates cache, a short-lived in-memory cache in front of Redis would remove most of those calls (see [Production considerations](#currency-rates-cache-1)).

**Split wallets across databases.** If writes ever outgrow one SQL Server primary, even after all of the above, wallets could be split across several databases by wallet ID. That's a big step: routing, migrations and reporting all get harder. I'd only consider it once the options above have run out.

The ECB sync job doesn't need to scale. It runs once a minute on a single node and writes about 30 rows in one statement, no matter how much traffic the API gets.

## Known limitations and next steps

What a production version would add, roughly in order of importance:

1. **A transaction ledger.** Adjustments change the wallet's balance directly, and there's no history of credits and debits. A real wallet would record every adjustment in an append-only ledger (amount, currency, rate used, strategy, idempotency key), with the balance calculated from it or checked against it. That's what makes audits, statements and disputes possible.
2. **Authentication and authorisation.** Right now any client can read or adjust any wallet. The next step is to authenticate callers, check that they own the wallet, and rate limit per user or API key instead of per IP.
3. **Observability.** There are [health checks](#health-checks), but no metrics yet (request rates, `429`s, cache hit rate, sync duration and failures) and no distributed tracing. OpenTelemetry would cover both.
4. **Deployment.** Run migrations as their own deployment step instead of at startup, publish the Docker image from CI, and use a secret store and a SQL login with only the permissions it needs. See [Production considerations](#production-considerations).
5. **Rates before the first sync.** Right after the very first start, before the job has run, conversions return `400 unsupported_currency`. A `503` with `Retry-After` would describe the situation better.

## Production considerations

The app is built to run as several replicas behind a load balancer, as the [load-balanced setup](#load-balanced-setup) shows. Rate limit counters live in Redis, and the ECB sync job uses a clustered Quartz job store in SQL Server, so each trigger runs on exactly one node. A real deployment would also need the following.

### Load balancer and client IP

- Trust exactly your proxies and nothing more. Set `ForwardedHeaders:KnownProxies` or `ForwardedHeaders:KnownNetworks` to your load balancer's addresses, and `ForwardLimit` to the number of proxy hops in front of the app. Without this, all clients share the load balancer's budget. Don't trust a whole network you don't control, or clients will be able to fake their IP.
- The first proxy decides the client IP. It has to either overwrite `X-Forwarded-For` with the connecting address (in nginx, `proxy_set_header X-Forwarded-For $remote_addr;`) or append to it. The app only reads the right-most `ForwardLimit` entries, so anything a client adds itself is ignored either way.
- Terminate TLS at the load balancer and forward `X-Forwarded-Proto`, which the app already understands.

### Rate limiting

- This limits fair usage. It doesn't protect against DDoS, because every rejected request still reaches the app and Redis. Put something in front for that, such as a WAF, a CDN or the cloud load balancer's own rate rules.
- Clients sharing an IP share a budget. Users behind a corporate NAT or a mobile carrier's gateway all look like one IP. Once the API has authentication, limit per user or API key, and keep the IP limit for anonymous traffic.
- Letting requests through when Redis is down is a deliberate choice. The app keeps serving without limits and logs `Rate limiter store is unavailable`, so set up an alert on that line. If abuse is worse than downtime for an endpoint, change it to fail closed in [FailOpenRateLimiter.cs](src/App.Host/Infrastructure/RateLimiting/FailOpenRateLimiter.cs).
- Keep the nodes' clocks in sync (NTP). Window start and expiry times come from each node's clock, so drift between nodes shifts the window boundaries.
- Tune the limits from real traffic. Watch the `Rate limit exceeded` warnings. Limits can be changed per environment through configuration, with no code change.

### Redis

- Use a managed, highly available Redis, such as AWS ElastiCache, Azure Cache for Redis, or Sentinel/Cluster. The rate limit keys use hash tags (`rl:fw:{client|endpoint}`), so they work with Redis Cluster.
- Turn on authentication and TLS through the connection string, e.g. `my-redis:6380,password=...,ssl=true`.
- Memory use stays small. Each active client and endpoint pair uses two small keys, which Redis deletes when their window ends, and the rates cache is one hash of about 30 fields. Set a `maxmemory` policy anyway, and pick `volatile-lru` or `allkeys-lru` rather than `noeviction`. Every key the app writes has a TTL, and a full Redis should evict keys rather than refuse writes.

### Deployment

- Deploy the image, not the source. The [Dockerfile](Dockerfile) already builds a production image, so publish it from CI with a version tag instead of building on the server.
- Keep secrets out of the repo. Inject connection strings from a secret store (Key Vault, AWS Secrets Manager, Kubernetes secrets) rather than `.env`, and use a SQL login with limited permissions instead of `sa`.
- Run migrations as a separate deployment step rather than at app startup. The app's SQL login then doesn't need permission to change the schema, and a failed migration stops the deployment instead of crash-looping every replica.
- Point the load balancer and orchestrator at the [health checks](#health-checks): `/health` for readiness and routing, `/health/live` for liveness. Restrict who can reach them if the API is public.

### Currency rates cache

- Alert on `Currency rates cache is unavailable`. Requests keep working by reading from the database, but then all conversion traffic hits SQL Server.
- Rates can be at most one job interval out of date. Replicas never disagree, because they all read the same snapshot. After a Redis outage, a snapshot that Redis restored from disk is served until the next job run, at most a minute later. The ECB only publishes new rates once a day, so that's fine.
- An empty cache isn't protected against a stampede. While it's empty (only until the first job run after startup, or if the key is lost), concurrent requests each run the small, indexed "latest rates" query. If conversion traffic grows enough for that to matter, let one request per node do the query while the rest wait for it, or fill the cache at startup.
- If Redis latency ever matters, a short-lived in-memory cache in front of Redis would remove the network hop, at the cost of each node lagging by up to that cache's lifetime. It isn't needed at the current load.
- I looked at HybridCache and decided against it. Its in-memory layer isn't updated on the other nodes when the job writes new rates, so nodes could briefly serve different rates.
