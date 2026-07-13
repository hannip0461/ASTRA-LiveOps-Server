# PostgreSQL 연결 풀 및 SLO

## 연결 예산

각 process는 이름이 지정된 `NpgsqlDataSource` 하나를 소유한다. Request code는 해당 pool을 재사용하며 요청마다 connection string을 만들지 않는다.

| Process | 기본 최대값 | 근거 |
|---|---:|---|
| `Astra.Api` | 8 | Admin audit와 Outbox 운영, player command는 Orleans 경유 |
| `Astra.Silo` | 24 | Player transaction hot path와 장기 실행 content `LISTEN` connection |
| `Astra.Worker` | 8 | Outbox handler 4개와 lease/maintenance 여유 |

Local topology의 application connection 상한은 40개다. `MinimumPoolSize=0`이므로 idle startup에서 connection을 미리 할당하지 않는다.

Replica 수를 변경하기 전에 다음 조건을 만족해야 한다.

```text
sum(replica count * process MaximumPoolSize)
  <= PostgreSQL max_connections - admin/migration/probe reserve
```

복구와 운영을 위해 최소 20% 여유를 유지한다. Replica가 늘면 process별 pool을 줄이거나 external pooler를 사용한다. PostgreSQL memory와 query concurrency 검토 없이 `max_connections`만 늘리지 않는다. PgBouncer transaction pool을 도입해도 Silo의 session-scoped `LISTEN` connection은 direct 또는 session-pooled route를 사용해야 한다.

## 실행 제한

API, Silo, Worker의 설정은 `Astra:Postgres`에 있다.

- Pool wait/connection timeout: 3초
- Command timeout: 15초
- Idle lifetime: 60초
- Pruning interval: 10초
- Physical connection lifetime: 30분
- 시작 검증에서 뒤집힌 pool bound, 1초 미만 값과 허용 범위 밖 값을 거부
- 설정값이 connection string에 포함된 상충 pool parameter보다 우선

Application의 connection 획득 경로는 `PostgresConnectionExtensions.OpenConnectionObservedAsync`로 통일한다. Caller cancellation은 dependency failure metric에 포함하지 않고 그대로 전달한다.

## 관측 지표

API, Silo와 Worker는 기존 OTLP exporter를 통해 Npgsql trace와 metric을 전송한다. Data source name에는 connection string 대신 안정적인 service name을 사용해 credential 노출을 방지한다.

| Metric | 용도 |
|---|---|
| `db.client.connection.count{state=used|idle}` | Pool state별 connection 수 |
| `db.client.connection.max` | 설정된 pool 상한 |
| `db.client.operation.duration` | PostgreSQL command latency |
| `astra.postgres.connection.acquire.attempts` | Connection 획득 시도 수 |
| `astra.postgres.connection.acquire.duration` | Physical open 또는 pool wait 시간 |
| `astra.postgres.connection.acquire.failures{reason}` | `timeout`, `postgres`, `transport`, `unknown` 실패 |
| `astra.tcp.server.requests{rpc.method,outcome}` | TCP response 전달 결과 |
| `astra.tcp.server.request.duration` | TCP command부터 response 전달 완료까지의 시간 |

OTLP Counter는 delta temporality를 사용한다. Pool utilization은 `service.name`, `db.client.connection.pool.name`별 `used / max`로 계산한다. Player ID, event ID, SQL parameter와 idempotency key는 metric label로 사용하지 않는다.

## 초기 SLO

다음 값은 운영 traffic 측정치가 아니라 초기 engineering target이다. 대표 부하의 soak test 이후 다시 조정한다.

| 목표 | 기준 | 측정 방식 |
|---|---|---|
| 유효 game command 가용성 | 30일 99.9% | Client/domain 거부를 제외한 HTTP 및 TCP server/dependency failure 비율 |
| PostgreSQL 획득 성공률 | 30일 99.9% | `1 - timeout failures / attempts` |
| PostgreSQL 획득 latency | 5분 p95 <= 100 ms | `astra.postgres.connection.acquire.duration` |
| PostgreSQL operation latency | 5분 p95 <= 250 ms | `db.client.operation.duration` |
| Pool 여유 | 5분 window의 99%에서 used/max < 85% | Process pool별 Npgsql gauge |

30일 기준 99.9% 가용성의 error budget은 약 43분이다. Acquisition burn rate는 다음과 같이 계산한다.

```text
(acquisition failures / acquisition attempts) / 0.001
```

운영 probe는 connection 2개인 pool의 두 slot을 점유한다. 독립 실행에서 attempt 4회, timeout 1회, 주입 failure rate 25%, burn rate 250x와 회복 query 성공을 기록한다. 이 수치는 운영 성능 측정이 아니라 fault injection 결과다.

## 경보 기준

| 등급 | 조건 | Window | 초기 대응 |
|---|---|---|---|
| Warning | pool utilization >= 75% | 10분 | Service/pool과 slow command 확인 |
| Critical | pool utilization >= 90% | 2분 | 비필수 batch 중단, block/long transaction 확인 |
| Critical | acquisition `reason=timeout` >= 1 | 5분 | 포화, DB 연결과 pool leak 확인 |
| Warning | acquisition p95 > 100 ms | 5분 | used/max와 DB operation latency 비교 |
| Critical | acquisition p95 > 500 ms | 2분 | Command timeout 임박으로 처리 |
| Warning | DB operation p95 > 250 ms | 5분 | Trace와 normalized statement 확인 |
| Critical | DB operation p95 > 1 s | 2분 | Lock, CPU/I/O와 query plan 확인 |
| Critical | eligible server/dependency error > 1%, request >= 100 | 5분 | API/TCP, Silo, PostgreSQL trace 연결 확인 |
| Warning | `astra.outbox.cycle_failures` 증가 | 5분 | Worker와 DB 가용성 확인 |
| Critical | `astra.outbox.dead_lettered` 증가 | 즉시 | Payload/consumer 원인 확인 후 감사 replay |

Idle pool ratio나 단일 slow command만으로 page를 발생시키지 않는다. Saturation은 acquisition latency/failure와 함께 평가한다.

## 검증

실제 PostgreSQL container를 사용하는 integration test가 다음 동작을 검증한다.

1. 점유된 slot 2개 때문에 세 번째 획득이 1초 이내 timeout되고 `reason=timeout`을 기록한 뒤 slot 반환 후 회복한다.
2. 짧은 command 24개가 slot 4개 pool을 통과하며 PostgreSQL session은 4개를 넘지 않는다.

`scripts/observability/Test-OperationalScenarios.ps1`은 같은 saturation 경로를 OTLP로 전송하고 burn-rate dashboard data를 확인한다.

```powershell
$env:ASTRA_RUN_POSTGRES_TESTS='1'
dotnet test tests/Astra.IntegrationTests/Astra.IntegrationTests.csproj --filter PostgresPoolSaturationTests
```
