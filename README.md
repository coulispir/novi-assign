# novi-assign

Wallet management API (ASP.NET Core, .NET 10) backed by SQL Server, with ECB exchange rate synchronisation via Quartz, a Redis cache of the latest rates, and per-client rate limiting via Redis.

## Running locally

```bash
cp .env.sample .env
docker compose up -d
```

This starts SQL Server, Redis and the API on `http://localhost:${APP_PORT}` (default `5000`). Example requests are in [tests/requests.sh](tests/requests.sh).

The default Compose setup is a development environment: it runs the API from source with `dotnet watch` (hot reload) and connects to SQL Server as `sa`. SQL Server data is kept in the `mssql-data` volume; `docker compose down -v` wipes it. See [Production considerations](#production-considerations) for what changes in a real deployment.

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

## Rate limiting

Each client IP can make a limited number of requests per time window to each endpoint. Requests over the limit are rejected with `429 Too Many Requests`, a `Retry-After` header (seconds) and a JSON body:

```json
{ "error": "Too many requests. Please retry later." }
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

## Currency rates cache

Currency conversion (`GET /api/wallets/{walletId}?currency=USD`) reads exchange rates from Redis, not from SQL Server. The database is only queried when the cache is empty or unreachable.

```
EcbSyncJob (every minute, one node) ──► SQL Server ──► latest rate per currency ──► Redis (replace snapshot)
GET /api/wallets/{id}?currency=X ──► Redis ──hit──► convert
                                        └──miss──► SQL Server ──► fill Redis if still empty ──► convert
```

### How it works

- **One shared snapshot in Redis, not an in-process cache.** The sync job runs on only one node per trigger, so an in-memory cache would be refreshed on that node and stay stale on every other one. Redis gives all nodes the same rates as soon as the job finishes.
- **The snapshot is a single hash** at `currency-rates:latest:v1`, mapping each currency code to its latest rate against EUR. Reading it is one `HGETALL` of about 30 small fields. The `v1` suffix lets a future format change roll out without old and new nodes misreading each other's data.
- **The job refreshes the cache on every run**, right after saving rates to SQL Server, even when no rate changed. The snapshot is rebuilt from the database (the latest rate for each currency), not from the ECB payload, so the cache always matches the database. It is replaced atomically in one `MULTI/EXEC` transaction: readers never see a half-written snapshot, and currencies no longer in the database are dropped. Because every run rewrites it, the cache recovers within one job interval after a Redis restart, eviction or outage.
- **Cache misses read through, without a race.** On a miss (for example at first startup, before the job has run), the request reads the rates from SQL Server and writes them to Redis **only if the key still doesn't exist** (a `WATCH`-based transaction). If the job writes a fresher snapshot while a request is still reading the database, the request's older data is discarded instead of overwriting it.
- **Unknown currencies never reach the database.** The snapshot holds every currency, so a currency missing from it has no rate, and the request fails with `400` without a database query.
- **Fails open.** If Redis is unreachable, requests read rates from SQL Server and a warning is logged (`Currency rates cache is unavailable`), rather than failing. As with rate limiting, Redis commands fail immediately while disconnected, so an outage adds no latency.

### Configuration

```json
"CurrencyRatesCache": {
  "TimeToLive": "01:00:00"
}
```

- `TimeToLive` is only a safety net, since the job rewrites the snapshot every minute. It makes sure the key never lives forever, and that Redis can evict it under a `volatile-*` `maxmemory` policy. Keep it longer than the job interval, or requests will fall back to the database between runs.
- The app refuses to start if `TimeToLive` is missing or not positive.

### Code

- [ICurrencyRatesProvider](src/Core.Service/Interfaces/ICurrencyRatesProvider.cs) / [CurrencyRatesProvider](src/Core.Service/Services/CurrencyRatesProvider.cs): the cache-aside read path used by `WalletService`, and the refresh used by `EcbSyncJob`.
- [ICurrencyRatesCache](src/Core.Service/Interfaces/ICurrencyRatesCache.cs): the cache abstraction, which keeps `Core.Service` free of Redis dependencies.
- [RedisCurrencyRatesCache](src/App.Host/Infrastructure/Caching/RedisCurrencyRatesCache.cs): the Redis implementation, registered by `AddCurrencyRatesCache`.

## Tests

```bash
dotnet test
```

The integration tests in [tests/Integration.Tests](tests/Integration.Tests) start a real Redis container with [Testcontainers](https://dotnet.testcontainers.org/), so **Docker must be running**.

The rate limiting tests host the real `WalletController` in memory with the production rate limiting setup and a mocked handler, so no database is needed. They cover:

- the 429 response
- per-endpoint limits
- shared budgets across route values and across two app nodes
- atomicity under 50 concurrent requests
- trusted and spoofed `X-Forwarded-For`
- IPv6 `/64` grouping
- failing open when Redis is down
- startup validation

The currency rates cache tests run the production cache registrations against the same Redis. They cover exact decimal round-trips, atomic replacement that drops removed currencies, the conditional read-through fill never overwriting a fresher snapshot, the TTL, unreadable data, failing open when Redis is down, and startup validation.

The unit tests in [tests/Unit.Tests](tests/Unit.Tests) cover the cache-aside logic in `CurrencyRatesProvider` and check that `EcbSyncJob` refreshes the cache only after a successful sync.

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
