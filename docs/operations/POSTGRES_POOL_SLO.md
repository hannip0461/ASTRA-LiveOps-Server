# PostgreSQL Pool And SLO

## Connection Budget

Each process owns one named `NpgsqlDataSource`; request code reuses that pool and never constructs a connection string per request.

| Process | Default maximum | Reason |
| --- | ---: | --- |
| `Astra.Api` | 8 | Admin audit and outbox operations; player commands go through Orleans |
| `Astra.Silo` | 24 | Player transaction hot path plus one long-lived content `LISTEN` connection |
| `Astra.Worker` | 8 | Four concurrent outbox handlers plus lease and maintenance headroom |

The one-process local topology therefore reserves at most 40 application connections instead of inheriting Npgsql's 100-per-pool default. `MinimumPoolSize` remains zero, so idle startup does not preallocate those slots.

Before changing replica counts, enforce:

```text
sum(replica count * process MaximumPoolSize)
  <= PostgreSQL max_connections - admin/migration/probe reserve
```

Use at least 20% headroom for recovery and operations. More replicas require smaller per-process pools or an external pooler; increasing PostgreSQL `max_connections` without a memory and query-concurrency budget is not the default response. A future PgBouncer transaction pool must not carry the Silo's session-scoped `LISTEN` connection; that connection needs a direct or session-pooled route.

## Runtime Guardrails

Configuration lives under `Astra:Postgres` in API, Silo, and Worker settings.

- pool wait/connection timeout: 3 seconds;
- command timeout: 15 seconds;
- idle lifetime: 60 seconds;
- pruning interval: 10 seconds;
- physical connection lifetime: 30 minutes;
- startup validation rejects inverted pool bounds, fractional-second values, and unsafe ranges;
- the configured values override conflicting pool parameters embedded in the connection string.

`PostgresConnectionExtensions.OpenConnectionObservedAsync` is the only application connection-acquisition path. Cancellation initiated by the caller is propagated without being counted as a dependency failure.

## Signals

API, Silo, and Worker export Npgsql traces and metrics through the existing OTLP exporter. Data-source names are stable service names rather than connection strings, so telemetry does not expose credentials.

| Metric | Use |
| --- | --- |
| `db.client.connection.count{state=used|idle}` | Connections by pool state |
| `db.client.connection.max` | Configured pool ceiling |
| `db.client.operation.duration` | PostgreSQL command latency |
| `astra.postgres.connection.acquire.attempts` | Connection acquisition denominator for availability calculations |
| `astra.postgres.connection.acquire.duration` | Physical-open or pool-wait latency in seconds |
| `astra.postgres.connection.acquire.failures{reason}` | `timeout`, `postgres`, `transport`, or `unknown` acquisition failures |
| `astra.tcp.server.requests{rpc.method,outcome}` | Delivered TCP responses by success, rejection, timeout, or server/transport error |
| `astra.tcp.server.request.duration` | TCP command-to-response-delivery latency in seconds |

OTLP Counter instruments are exported with delta temporality so rate and alert windows represent new events in each collection interval.

Pool utilization is `used / max`, grouped by `service.name` and `db.client.connection.pool.name`. Never add player IDs, event IDs, SQL parameters, or idempotency keys as metric labels.

## Initial SLOs

These are initial engineering targets, not claims from production traffic. Re-baseline them after a representative soak test.

| Objective | Target | Measurement |
| --- | --- | --- |
| Valid game-command availability | 99.9% over 30 days | Server/dependency failures divided by eligible HTTP requests and TCP `success`, `timeout`, `server_error`, or `transport_error` outcomes; client/domain rejections excluded |
| PostgreSQL acquisition success | 99.9% over 30 days | `1 - timeout acquisition failures / acquisition attempts` |
| PostgreSQL acquisition latency | p95 <= 100 ms over 5 minutes | `astra.postgres.connection.acquire.duration` |
| PostgreSQL operation latency | p95 <= 250 ms over 5 minutes | `db.client.operation.duration` |
| Pool headroom | used/max < 85% in 99% of 5-minute windows | Npgsql connection gauges per process pool |

A 99.9% monthly availability objective permits about 43 minutes of bad time in a 30-day month. The alert windows below are deliberately shorter than that budget.

Acquisition burn rate is calculated as:

```text
(acquisition failures / acquisition attempts) / 0.001
```

The operational probe deliberately holds both slots of a two-connection pool. Its isolated run records four attempts, one timeout, a 25% injected failure rate, a 250x burn rate, and a successful recovery query. This is fault-injection evidence, not a production performance claim.

## Alert Thresholds

| Severity | Condition | Window | First action |
| --- | --- | --- | --- |
| Warning | pool utilization >= 75% | 10 min | Identify service/pool and slow commands |
| Critical | pool utilization >= 90% | 2 min | Stop nonessential batch work; inspect blocked/long transactions |
| Critical | acquisition `reason=timeout` >= 1 | 5 min | Check saturation, database reachability, and pool leaks |
| Warning | acquisition p95 > 100 ms | 5 min | Compare used/max and DB operation latency |
| Critical | acquisition p95 > 500 ms | 2 min | Treat as imminent command timeout |
| Warning | DB operation p95 > 250 ms | 5 min | Inspect traces and top normalized statements |
| Critical | DB operation p95 > 1 s | 2 min | Check locks, CPU/I/O, and query plans |
| Critical | eligible server/dependency error ratio > 1% with >= 100 requests | 5 min | Correlate API/TCP traces with Silo and PostgreSQL spans |
| Warning | `astra.outbox.cycle_failures` increases | 5 min | Inspect Worker and database availability |
| Critical | `astra.outbox.dead_lettered` increases | immediate | Triage payload/consumer failure before audited replay |

Do not page from an idle pool ratio or a single slow command alone. Pair saturation with acquisition latency/failures to distinguish healthy pool reuse from user-visible queueing.

## Verification

The integration suite uses an isolated pool with a real PostgreSQL container. It proves both behaviors:

1. two held slots make a third acquisition fail within its one-second budget, emit `reason=timeout`, and recover after a slot is returned;
2. 24 concurrent short commands queue through a four-slot pool without creating more than four PostgreSQL sessions.

`scripts/observability/Test-OperationalScenarios.ps1` exports the same saturation path through OTLP and verifies the resulting burn-rate dashboard data.

```powershell
$env:ASTRA_RUN_POSTGRES_TESTS='1'
dotnet test tests/Astra.IntegrationTests/Astra.IntegrationTests.csproj --filter PostgresPoolSaturationTests
```
