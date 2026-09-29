# novi-assign

Wallet management API (ASP.NET Core, .NET 10) backed by SQL Server, with ECB exchange rate synchronisation via Quartz, a Redis cache of the latest rates, and per-client rate limiting via Redis.

## Running locally

```bash
cp .env.sample .env
docker compose up -d
```

This starts SQL Server, Redis and the API on `http://localhost:${APP_PORT}` (default `5000`). Example requests are in [tests/requests.sh](tests/requests.sh).

The default Compose setup is a development environment: it runs the API from source with `dotnet watch` (hot reload) and connects to SQL Server as `sa`. SQL Server data is kept in the `mssql-data` volume; `docker compose down -v` wipes it. See [Production considerations](#production-considerations) for what changes in a real deployment.

### API documentation

In Development, the API publishes an [OpenAPI](https://www.openapis.org/) 3.0 document and a Swagger UI to browse and try it:

- Swagger UI: `http://localhost:5000/swagger`
- OpenAPI document: `http://localhost:5000/openapi/v1.json`. Share it, or generate clients from it.

The document is generated at runtime by ASP.NET Core's built-in `Microsoft.AspNetCore.OpenApi`, from the controllers' routes, parameters and `[ProducesResponseType]` attributes. Swagger UI (`Swashbuckle.AspNetCore.SwaggerUI`) only renders it. Both are registered in [ApiDocumentationExtensions.cs](src/App.Host/Infrastructure/ApiDocumentationExtensions.cs).

- **OpenAPI 3.0, not the .NET 10 default of 3.1.** Client generators (e.g. openapi-generator for Kotlin) support 3.0 more reliably.
- **Numbers are documented as plain numbers.** ASP.NET Core's JSON defaults also accept numbers sent as strings, so .NET generates every number as "number or string" (a type list in 3.1, `anyOf` in 3.0). Swagger UI can't fill in such a parameter and rejects every value as missing (`amount: Required field is not provided`), and generated clients would type amounts as strings. A schema transformer keeps only the number type. The API itself still accepts both.
- **Development only.** Production doesn't publish its API surface, so the [load-balanced setup](#load-balanced-setup), which runs as `Production`, has no `/swagger`.
- **Strategies are a dropdown.** `strategy` is the [BalanceStrategyType](src/Core.Service/Strategies/BalanceStrategyType.cs) enum, so the document lists its three names as a string `enum`. Swagger UI shows a dropdown, and generated clients get a typed enum. Names bind ignoring case (`addfundsstrategy` works). Anything else is rejected with `400 invalid_request`, including numbers such as `strategy=0`, which ASP.NET would otherwise quietly map to the first strategy.
- **Every response must be declared.** A status code without a `[ProducesResponseType]` is missing from the contract. [OpenApiDocumentTests](tests/Integration.Tests/OpenApi/OpenApiDocumentTests.cs) checks the endpoints, the `Idempotency-Key` header, every status code of `adjustbalance` and the `ErrorResponse` schema.

### Load-balanced setup

To see the app running the way it would in production, layer [docker-compose.lb.yml](docker-compose.lb.yml) on top of the default setup:

```bash
docker compose -f docker-compose.yml -f docker-compose.lb.yml up -d --build
```

```
client ──► nginx :5000 ──round-robin──► webapi replica 1 ─┐
                                    └─► webapi replica 2 ─┴─► SQL Server + Redis
```

- **nginx** ([deploy/nginx/nginx.conf](deploy/nginx/nginx.conf)) is the only entry point, on `${APP_PORT}`. It spreads requests across the replicas, sets `X-Forwarded-For` to the connecting address (replacing anything the client sent) and adds an `X-Upstream` response header showing which replica answered.
- **Two API replicas** run the production image from the [Dockerfile](Dockerfile): the ASP.NET runtime only, as a non-root user, in the `Production` environment. They trust `X-Forwarded-For` only from nginx's fixed address, `172.28.0.10`.
- **SQL Server and Redis are shared** with the default setup, including data.

Try it: send 12 requests and watch them alternate between replicas while the `wallet-read` limit (10 per second) still applies to the client as a whole:

```bash
for i in $(seq 12); do curl -s -o /dev/null -w "%{http_code} via %header{x-upstream}\n" http://localhost:5000/api/wallets/1; done
```

The ECB sync job runs on only one replica per trigger, because Quartz uses a clustered job store.

Things to know:

- **All local requests look like one client.** Docker Desktop hands nginx connections from its gateway (e.g. `172.28.0.1`), not your machine's real IP. That's enough to show the limit holding across replicas; the integration tests cover different clients and faked headers.
- **The image doesn't hot reload.** It's built once, so rebuild with `--build` after code changes.
- **Switch back to the hot-reload setup** with `docker compose up -d --remove-orphans`.

## Errors

Every error the API returns has the same JSON body:

```json
{ "error": "Wallet lacks sufficient funds to complete this operation.", "code": "insufficient_funds" }
```

`code` is stable, so clients should branch on it. `error` is a human-readable message and may change.

| Status | `code` | When |
|---|---|---|
| 400 | `invalid_request` | Invalid input: a missing or unparsable parameter, malformed JSON, an unknown strategy, non-positive amount, a blank or too long `Idempotency-Key`, bad currency code, negative initial balance, an amount in another currency that converts to less than 0.0001 of the wallet's currency |
| 400 | `unsupported_currency` | No exchange rate is known for the requested conversion or adjustment currency |
| 404 | `wallet_not_found` | The wallet doesn't exist |
| 409 | `concurrency_conflict` | Another request changed the wallet at the same time. Nothing was applied: retry the request (with the same `Idempotency-Key`, if you sent one) |
| 422 | `insufficient_funds` | `SubtractFundsStrategy` would take the balance below zero |
| 422 | `idempotency_key_reused` | The `Idempotency-Key` was already used for a different request |
| 429 | `rate_limited` | Rate limit exceeded; see [Rate limiting](#rate-limiting) |
| 500 | `internal_error` | Anything unexpected. The details are logged, never returned |

### How it works

- **One place maps exceptions to responses.** [ApiExceptionFilter](src/Apis/Wallet.Api/Filters/ApiExceptionFilter.cs) is applied to `WalletController`, so the actions only handle the success path. `Core.Service` throws domain exceptions ([Core.Service/Exceptions](src/Core.Service/Exceptions)) and knows nothing about HTTP.
- **Only expected failures are 4xx.** Anything not in the table, such as a SQL Server outage or a bug, is a `500` with a generic message. Clients aren't told it's their fault, monitoring sees a server error, and internal details (e.g. SQL error text) never reach the response. The exception is logged as `Unhandled exception while processing {Method} {Path}`.
- **Client errors have their own exception type.** Input the client got wrong throws `DomainValidationException`. `ArgumentException` stays for guard clauses that catch programming errors, and those are `500`s.
- **Cancelled requests aren't errors.** If the client disconnects, the filter doesn't log an error or write a response.
- **Malformed requests use the same body.** ASP.NET Core rejects a missing required parameter, an unparsable value or malformed JSON before the action runs. `AddWalletApi` ([WalletApiServiceCollectionExtensions.cs](src/Apis/Wallet.Api/WalletApiServiceCollectionExtensions.cs)) replaces the default problem details with the same `ErrorResponse` (`invalid_request`), prefixing each message with the parameter name (e.g. `strategy: The value 'Transfer' is not valid.`). Clients handle one error shape.

To add an error: create an exception in `Core.Service/Exceptions`, throw it from the domain code, add a code to [ErrorCodes](src/Apis/Wallet.Api/Models/ErrorResponse.cs), map it in `ApiExceptionFilter`, add a case to [ErrorResponseTests](tests/Integration.Tests/ErrorHandling/ErrorResponseTests.cs), and cover it end to end in [Functional.Tests](tests/Functional.Tests).

## Rate limiting

Each client IP can make a limited number of requests per time window to each endpoint. Requests over the limit are rejected with `429 Too Many Requests`, a `Retry-After` header (seconds) and a JSON body:

```json
{ "error": "Too many requests. Please retry later.", "code": "rate_limited" }
```

To see every limit in action against a running stack, run `tests/rate-limit.sh` (optionally passing a base URL; the default is `http://localhost:5000`).

### How it works

- **Built-in ASP.NET Core rate limiting middleware** (`AddRateLimiter` / `[EnableRateLimiting]`), with counters stored in Redis through [RedisRateLimiting](https://github.com/cristipufu/aspnetcore-redis-rate-limiting). With in-memory counters, every node behind a load balancer would give each client its own separate allowance; Redis makes the limit hold across all nodes.
- **Fixed window algorithm.** A window starts with a client's first request and resets once it expires. It uses constant memory per client and gives an exact `Retry-After`. The trade-off: a client can send up to twice the limit in a short burst across a window boundary.
- **No race conditions.** Each check-and-increment runs as a single atomic Lua script in Redis, so concurrent requests, even on different nodes, cannot exceed the limit.
- **One budget per client IP per endpoint.** Endpoints are identified by HTTP method and route pattern, so `/api/wallets/1` and `/api/wallets/2` share the same budget. IPv6 clients are limited per `/64` block, since one subscriber usually owns a whole block and could otherwise rotate addresses.
- **Fails open.** If Redis is unreachable, requests are allowed through and a warning is logged, rather than every request failing with a 500. Redis commands fail immediately while disconnected, so an outage adds no latency.

### Policies

Each endpoint opts into a named policy with `[EnableRateLimiting]`. Policy names are defined in [RateLimitPolicies.cs](src/Apis/Wallet.Api/RateLimitPolicies.cs) and their limits in `appsettings.json`:

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

- `Window` is a `TimeSpan` with one-second resolution; the minimum is `00:00:01`. Because the Redis window is tracked in whole seconds, a window can last up to one second longer than configured. The limit is then slightly stricter, never looser.
- The app refuses to start if a policy in `RateLimitPolicies.All` has no valid configuration.

To add a policy: add a constant to `RateLimitPolicies` and list it in `All`, configure it under `RateLimiting:Policies`, and put `[EnableRateLimiting(RateLimitPolicies.YourPolicy)]` on the endpoint.

### Client IP behind a load balancer

The real client IP is read from `X-Forwarded-For`, **but only when the request comes from a trusted proxy**. If the app trusted the header from anyone, a client could fake it to get past the limit. Only loopback is trusted by default, so before deploying behind a load balancer, list its addresses:

```json
"ForwardedHeaders": {
  "ForwardLimit": 1,
  "KnownProxies": [ "10.0.0.5" ],
  "KnownNetworks": [ "10.0.0.0/16" ]
}
```

- `KnownProxies` takes single addresses and `KnownNetworks` takes CIDR ranges.
- `ForwardLimit` is the number of proxy hops in front of the app.
- Until the load balancer is listed, the app sees the load balancer's IP, so all clients share one budget.
- The app refuses to start if an entry isn't a valid address or CIDR range, or `ForwardLimit` is below 1.

### Redis

The Redis connection string is `ConnectionStrings:Redis`, set to `redis:6379` in Docker Compose. One shared `IConnectionMultiplexer` is registered in [RedisServiceCollectionExtensions.cs](src/App.Host/Infrastructure/RedisServiceCollectionExtensions.cs). Rate limiting and the [currency rates cache](#currency-rates-cache) both use it, and any new Redis feature should too, rather than open a second connection.

## ECB rate sync

[EcbSyncJob](src/Core.Service/Jobs/EcbSyncJob.cs) runs on startup and then every minute by default (Quartz, on one node per trigger). It fetches the daily ECB feed through the [Ecb.Gateway](src/Ecb.Gateway/EcbClient.cs) library, saves the rates to SQL Server, then refreshes the [currency rates cache](#currency-rates-cache).

```
ECB feed ──► EcbClient (Ecb.Gateway: EcbDailyRates) ──► EcbGatewayAdapter (+ EUR, EcbRateResult) ──► LoggingEcbGatewayDecorator ──► EcbRatesService (validate, de-duplicate) ──► one MERGE ──► CurrencyValues
```

**The gateway is a standalone library.** `Ecb.Gateway` references no other project. It exposes `IEcbClient`, which returns the feed as typed objects (`EcbDailyRates`: the publication date and each `EcbRate`), so any application could use it. The core doesn't depend on it either: `Core.Service` defines the `IEcbGateway` port it needs, and [EcbGatewayAdapter](src/App.Host/Infrastructure/Ecb/EcbGatewayAdapter.cs) in the host connects the two. The adapter also adds EUR at 1, because every ECB rate is quoted against the euro and the feed doesn't list EUR itself.

`CurrencyValues` keeps **one row per currency per date**, so it holds the full history of rates. A new day adds rows; the same day again updates them.

### How it works

- **One raw SQL `MERGE` per sync.** [CurrencyValueRepository.MergeRatesAsync](src/Core.Service/Repositories/CurrencyValueRepository.cs) sends the whole feed in a single `MERGE INTO CurrencyValues` statement, matching on the unique `(CurrencyCode, RateDate)` index:
  - a date with no row for that currency is **inserted**;
  - a row whose rate changed is **updated** (rate and `UpdatedAt`);
  - an unchanged rate matches no `WHEN` clause, so the row isn't rewritten.

  One statement means one round trip and one transaction: all rates are saved, or none are. `OUTPUT $action` returns what happened to each row, which feeds the `Inserted` / `Updated` counts in the job's log line.
- **Parameters only.** The SQL text contains nothing but generated parameter names (`@c0, @r0, @d0, @u0, ...`). Every value is sent as a typed parameter matching its column (`char(3)`, `decimal(18,6)`, `date`, `datetime2`), so feed data can never change the statement and no precision is lost.
- **`WITH (HOLDLOCK)`** keeps the matched key range locked until the insert. Two merges running at the same time (e.g. a manual run during a scheduled one) can't both insert the same currency and date and fail on the unique index.
- **The feed is cleaned first.** `EcbRatesService` validates every rate through `CurrencyValue.Create` (a 3-letter code, a positive rate, the date only) and keeps one entry per currency and date. A `MERGE` fails if its source matches the same row twice. An invalid rate fails the whole sync before anything is written, and the job retries on its next run.
- **Limit:** SQL Server allows 2100 parameters per statement and each rate uses 4, so one merge takes at most 500 rates (`MaxRatesPerMerge`). The daily feed has about 30.
- **Safe to retry.** Running the same merge again changes nothing, so EF's retry on transient SQL errors can safely repeat it.
- **Every feed call is timed and logged by a decorator.** [LoggingEcbGatewayDecorator](src/Core.Service/Decorators/LoggingEcbGatewayDecorator.cs) wraps the `IEcbGateway` port and logs `Fetched 30 rates from the ECB feed for 2026-09-29 in 231 ms`, or a warning with the elapsed time and the exception when the feed fails. It passes results and exceptions through unchanged, so neither the adapter nor the job knows it's there. It's added with the built-in container: `AddEcbGateway` registers the adapter as itself, and `IEcbGateway` resolves to the decorator wrapping it. `FailOpenRateLimiter` uses the same pattern around the Redis rate limiter.

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

- `Ecb` binds to the gateway library's `EcbClientOptions`, registered by [AddEcbGateway](src/App.Host/Infrastructure/Ecb/EcbServiceCollectionExtensions.cs). `Timeout` applies to each request to the feed.
- `EcbSync:Interval` sets the Quartz trigger, registered by [AddEcbSyncJob](src/App.Host/Infrastructure/Jobs/EcbSyncServiceCollectionExtensions.cs). The trigger is stored in the clustered job store, and Quartz overwrites it on startup, so a new interval takes effect when the nodes restart. Keep `CurrencyRatesCache:TimeToLive` longer than the interval.
- Like every setting, each can be overridden per environment, e.g. `EcbSync__Interval=00:05:00`.
- The app refuses to start if the URL isn't an absolute http(s) URL, or the timeout or the interval is missing or not positive.

## Idempotent adjustments (optional)

`POST /api/wallets/{walletId}/adjustbalance?amount=&currency=&strategy=` works exactly as the assignment specifies, with no extra header. Each request applies its adjustment, so two identical requests apply twice, as with any plain `POST`.

To make retries safe (e.g. after a timeout, when the client can't tell whether the first attempt went through), send an optional `Idempotency-Key` header with a unique value such as a UUID:

```bash
curl -sS -i -X POST 'http://localhost:5000/api/wallets/1/adjustbalance?amount=5&currency=EUR&strategy=AddFundsStrategy' -H "Idempotency-Key: $(uuidgen)"
```

- **The first request with a key applies the adjustment and stores its result**, in the same transaction as the balance change.
- **A retry with the same key and the same parameters** isn't applied again. It returns the stored result with an `Idempotent-Replayed: true` header, even when the retries arrive in parallel.
- **The same key with different parameters** is rejected with `422 idempotency_key_reused`.
- **A key that is sent must be usable:** 1-100 characters and not blank, otherwise `400 invalid_request`. An empty header counts as no key.

Without a key, concurrent adjustments are still safe: the wallet's row version makes a conflicting update fail with `409 concurrency_conflict` instead of losing an update.

## Adjustments in another currency

A balance adjustment can be made in any currency with a known exchange rate, not only the wallet's own. For example, `amount=50&currency=USD` on a EUR wallet converts the 50 USD to EUR and then applies the strategy:

```bash
curl -sS -X POST 'http://localhost:5000/api/wallets/1/adjustbalance?amount=50&currency=USD&strategy=AddFundsStrategy'
```

- **Converted with the latest ECB rates**, through EUR (`amount / rate(from) * rate(to)`) and rounded to 4 decimal places, the precision balances are stored with. Balance conversion on `GET` uses the same [CurrencyConverter](src/Core.Service/Services/CurrencyConverter.cs), so the two never disagree. The rates come from the [currency rates cache](#currency-rates-cache).
- **Balance rules apply to the converted amount.** `SubtractFundsStrategy` compares the converted amount with the balance, so it rejects anything that would take the wallet below zero in its own currency (`422 insufficient_funds`).
- **The response is in the wallet's currency.** It returns the wallet's new balance, as for any adjustment.
- **Failures leave the balance unchanged:** a currency without a rate is rejected with `400 unsupported_currency`, and an amount that converts to less than 0.0001 of the wallet's currency with `400 invalid_request`.
- **Retries replay the original result.** An adjustment retried with the same `Idempotency-Key` returns the balance from the first attempt, even if the rates changed in between. It is never converted again.

## Currency rates cache

Currency conversion (`GET /api/wallets/{walletId}?currency=USD`, and adjustments in another currency) reads exchange rates from Redis, not from SQL Server. The database is only queried when the cache is empty or unreachable.

```
EcbSyncJob (every EcbSync:Interval, one node) ──► SQL Server ──► latest rate per currency ──► Redis (replace snapshot)
GET /api/wallets/{id}?currency=X ──► Redis ──hit──► convert
                                        └──miss──► SQL Server ──► fill Redis if still empty ──► convert
```

### How it works

- **One shared snapshot in Redis, not an in-process cache.** The sync job runs on only one node per trigger, so an in-memory cache would be refreshed on that node and stay stale on every other one. Redis gives all nodes the same rates as soon as the job finishes.
- **The snapshot is a single hash** at `currency-rates:latest:v1`, mapping each currency code to its latest rate against EUR. Reading it is one `HGETALL` of about 30 small fields. The `v1` suffix lets a future format change roll out without old and new nodes misreading each other's data.
- **The job refreshes the cache on every run**, right after saving rates to SQL Server, even when no rate changed. The snapshot is rebuilt from the database (the latest rate for each currency), not from the ECB payload, so the cache always matches the database. It is replaced atomically in one `MULTI/EXEC` transaction: readers never see a half-written snapshot, and currencies no longer in the database are dropped. Because every run rewrites it, the cache recovers within one job interval after a Redis restart, eviction or outage.
- **Cache misses read through, without a race.** On a miss (for example at first startup, before the job has run), the request reads the rates from SQL Server and writes them to Redis **only if the key still doesn't exist** (a `WATCH`-based transaction). If the job writes a fresher snapshot while a request is still reading the database, the request's older data is discarded instead of overwriting it.
- **Unknown currencies never reach the database.** The snapshot holds every currency, so a currency missing from it has no rate, and the request fails with `400` (`unsupported_currency`) without a database query.
- **Fails open.** If Redis is unreachable, requests read rates from SQL Server and a warning is logged (`Currency rates cache is unavailable`), rather than failing. As with rate limiting, Redis commands fail immediately while disconnected, so an outage adds no latency.

### Configuration

```json
"CurrencyRatesCache": {
  "TimeToLive": "01:00:00"
}
```

- `TimeToLive` is only a safety net, since the job rewrites the snapshot on every run (every minute by default). It makes sure the key never lives forever, and that Redis can evict it under a `volatile-*` `maxmemory` policy. Keep it longer than the job interval, or requests will fall back to the database between runs.
- The app refuses to start if `TimeToLive` is missing or not positive.

### Code

- [ICurrencyRatesProvider](src/Core.Service/Interfaces/ICurrencyRatesProvider.cs) / [CurrencyRatesProvider](src/Core.Service/Services/CurrencyRatesProvider.cs): the cache-aside read path used by `WalletService`, and the refresh used by `EcbSyncJob`.
- [ICurrencyRatesCache](src/Core.Service/Interfaces/ICurrencyRatesCache.cs): the cache abstraction, which keeps `Core.Service` free of Redis dependencies.
- [RedisCurrencyRatesCache](src/App.Host/Infrastructure/Caching/RedisCurrencyRatesCache.cs): the Redis implementation, registered by `AddCurrencyRatesCache`.

## Tests

```bash
dotnet test                                # everything; Docker must be running
dotnet test tests/Unit.Tests               # unit tests only: no Docker, well under a second
```

### CI

[GitHub Actions](.github/workflows/ci.yml) runs on every pull request to `master` and every push to `master`, with two jobs in parallel:

- **Build and test** builds in Release (warnings are errors, so the analysers and code style are enforced too) and runs all three test projects. The integration and functional tests start Redis and SQL Server with Testcontainers, using the runner's Docker. The run page shows a coverage summary (line and branch coverage per assembly, excluding test projects and EF migrations). It's informational: there's no minimum. If tests fail, the TRX results and coverage files are attached to the run as the `test-results` artifact.
- **Docker image** builds the production [Dockerfile](Dockerfile) without pushing it, so a broken image (e.g. a new project missing from the restore layer) fails the PR.

A new push to the same PR cancels the run it replaces. To make the checks mandatory, require both jobs in a branch protection rule for `master`.

The tests follow the layers of the code, and each layer is tested with the lightest setup that can still catch its bugs:

| Project | What it tests | Real dependencies |
|---|---|---|
| [Unit.Tests/Domain](tests/Unit.Tests/Domain) | Entities and balance strategies: validation, balance rules | none, and no mocks |
| [Unit.Tests/Application](tests/Unit.Tests/Application) | Handler, services and sync job: coordination and business logic | none; ports mocked with NSubstitute |
| [Integration.Tests](tests/Integration.Tests) | Infrastructure adapters: Redis cache, rate limiting, ECB feed parsing, startup validation | Redis ([Testcontainers](https://dotnet.testcontainers.org/)); HTTP stubbed |
| [Functional.Tests](tests/Functional.Tests) | The API end to end, through `WebApplicationFactory<Program>` | SQL Server and Redis containers; only the ECB feed is faked |

### Unit tests

- **Domain:** wallet creation and credit/debit/force-debit rules, currency rate validation, each balance strategy, and strategy lookup, including which domain exception each rule throws (see [Errors](#errors)).
- **Application:**
  - what the ECB sync hands to the merge (the whole feed in one call, normalised, de-duplicated, invalid rates rejected, empty feed skipped);
  - conversion maths through EUR, including rounding, in `CurrencyConverter`;
  - handler input validation;
  - cache-aside reads;
  - the job refreshing the cache only after a successful sync.
- Names follow `Method_Scenario_Result`.

### Integration tests

- **Rate limiting** hosts the real `WalletController` with the production rate limiting setup and a mocked handler. It covers the 429 response, per-endpoint limits, shared budgets across route values and across two app nodes, atomicity under 50 concurrent requests, trusted and spoofed `X-Forwarded-For`, IPv6 `/64` grouping, and failing open when Redis is down.
- **Currency rates cache** runs the production cache registrations against Redis. It covers exact decimal round-trips, atomic replacement, the conditional read-through fill, the TTL, unreadable data, and failing open.
- **ECB gateway** parses canned feed responses: every rate plus the EUR base, malformed entries, parsing independent of culture, and error statuses.
- **Startup validation** checks that bad trusted-proxy settings and missing connection strings stop the host.
- **Error handling** hosts the real `WalletController` with a handler that throws. It checks that each domain exception returns its status code and `code`, and that unexpected exceptions (including a stray `ArgumentException`) return a generic `500` without leaking the exception message. No Redis or database needed.

### Functional tests

These boot the real `Program.cs` (DI, middleware, migrations) against real SQL Server and Redis containers:

- **Wallet lifecycle:** create, read, each strategy, and every error path (400, 404, and 422 for insufficient funds), each checked against its [error code](#errors). Failed requests leave the balance unchanged.
- **ECB rate merge:** the raw SQL `MERGE` against SQL Server. It inserts missing dates, updates only changed rates (unchanged rows keep their `UpdatedAt`), keeps the history per date, does nothing on a repeat, stores the full `decimal(18,6)` precision, and rejects more rates than one statement can carry.
- **Idempotency and concurrency:** replays, key reuse with a different request (422), parallel retries with the same key applied exactly once, and parallel adjustments never losing an update.
- **Currency conversion:** the full path from ECB feed to sync job, SQL Server, Redis and the endpoint. Also: rates are served from Redis rather than SQL Server, an empty cache falls back to the database and refills, new rates are served after a sync, and a currency the ECB drops keeps its last rate. Adjustments in another currency: credits and debits at the synced rate (including between two non-EUR currencies), the overdraft rule checked on the converted amount, and unknown currencies or amounts too small to convert rejected without changing the balance.

A few design choices keep these tests reliable:

- **Real SQL Server, not EF Core's in-memory provider.** Idempotency and lost-update protection depend on SQL Server enforcing the wallet row version and the idempotency key's primary key. The in-memory provider doesn't enforce those, so these tests would pass there even with the protections broken.
- **The Quartz scheduler doesn't run.** Tests call the real `EcbSyncJob` when they need a sync, instead of racing a background trigger.
- **Tests never depend on each other's data.** Each creates its own wallets, and each test that changes rates owns one currency (see [FakeEcbFeed](tests/Functional.Tests/Infrastructure/FakeEcbFeed.cs)). No database reset is needed between tests.
- **The tests keep their own copies of the response contracts**, so renaming an API property breaks them the same way it would break clients.
- **Test logs go to the test project's `bin/.../logs/`**, not `src/App.Host/logs`.

## Production considerations

The code is built to run as several replicas behind a load balancer, as the [load-balanced setup](#load-balanced-setup) shows. Rate limit counters live in Redis, and the Quartz ECB sync job uses a clustered SQL Server job store, so each trigger runs on exactly one node. A real deployment would also need the following.

### Load balancer and client IP

- **Trust exactly your proxies.** Set `ForwardedHeaders:KnownProxies` or `ForwardedHeaders:KnownNetworks` to your load balancer's addresses, and `ForwardLimit` to the number of proxy hops in front of the app. Without this, every client shares the load balancer's budget. Don't trust a whole network you don't control, or clients can fake their IP.
- **The first proxy decides the client IP.** It must overwrite `X-Forwarded-For` with the connecting address (e.g. nginx `proxy_set_header X-Forwarded-For $remote_addr;`) or append to it. The app reads only the right-most `ForwardLimit` entries, so values a client adds itself are ignored either way.
- **Terminate TLS at the load balancer** and forward `X-Forwarded-Proto`, which the app already honours.

### Rate limiting

- **This is fair-usage limiting, not DDoS protection.** Every rejected request still reaches the app and Redis. Put volumetric protection in front of it, such as a WAF, CDN or cloud load balancer rate rules.
- **Shared IPs share a budget.** Users behind corporate NAT or mobile carrier gateways appear as one IP. Once the API has authentication, key the limit on the user or API key instead, and keep the IP limit for anonymous traffic.
- **Fail-open is a deliberate trade-off.** If Redis goes down, the app keeps serving without limits and logs `Rate limiter store is unavailable`. Alert on that log line. If abuse is worse than downtime for an endpoint, change it to fail closed in [FailOpenRateLimiter.cs](src/App.Host/Infrastructure/RateLimiting/FailOpenRateLimiter.cs).
- **Keep node clocks in sync (NTP).** Window start and expiry times come from each app node's clock, so clock drift between nodes shifts window boundaries.
- **Tune limits from real traffic.** Watch the `Rate limit exceeded` warnings, and remember that limits can be changed per environment through configuration without code changes.

### Redis

- **Use a managed, highly available Redis** (e.g. AWS ElastiCache, Azure Cache for Redis, or Sentinel/Cluster). Rate limit keys use hash tags (`rl:fw:{client|endpoint}`), so they work with Redis Cluster.
- **Enable authentication and TLS** through the connection string, e.g. `my-redis:6380,password=...,ssl=true`.
- **Memory stays small.** Each active client/endpoint pair uses two small keys, and Redis expires them automatically when their window ends. The currency rates cache is one hash of about 30 fields. Set a `maxmemory` policy anyway, and prefer `volatile-lru` or `allkeys-lru` over `noeviction`: every key the app writes has a TTL, and a full Redis should evict keys, not reject writes.

### Deployment

- **Deploy the image, not the source.** The [Dockerfile](Dockerfile) already builds a production image; publish it from CI with a versioned tag instead of building on the host.
- **Keep secrets out of the repo.** Inject connection strings from a secret store (e.g. Key Vault, AWS Secrets Manager, Kubernetes secrets) rather than `.env`, and use a least-privilege SQL login instead of `sa`.
- **Run migrations as a separate deployment step** rather than at app startup. The app's SQL login then needs no permission to change the schema, and a failed migration stops the deployment instead of crash-looping every replica.
- **Add health checks** (`/health` covering SQL Server and Redis) for the load balancer and orchestrator.

### Currency rates cache

- **Alert on `Currency rates cache is unavailable`.** Requests keep working through the database fallback, but all conversion traffic then hits SQL Server.
- **Staleness is bounded by the job interval.** Replicas never disagree on rates, because they all read the same snapshot. After a Redis outage, a snapshot Redis restored from disk is served until the next job run (at most one minute). ECB publishes new rates once a day, so this is well within tolerance.
- **A cold cache isn't single-flighted.** While the cache is empty (only until the first job run after startup, or after the key is lost), concurrent requests each run the small, indexed "latest rates" query. If conversion traffic grows enough for that to matter, add a per-node single-flight or warm the cache at startup.
- **An in-process L1 cache is the next step if Redis latency ever matters.** A short-TTL memory cache in front of Redis would remove the network hop, at the cost of each node lagging by up to that TTL. It isn't needed at today's load.
- **HybridCache was considered and not used.** Its in-memory tier isn't updated on other nodes when the job writes new rates, so nodes could briefly serve different rates.
