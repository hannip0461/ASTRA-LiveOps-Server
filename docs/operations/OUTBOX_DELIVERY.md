# Outbox 전달 정책

## 정합성 경계

플레이어 command는 domain state, ledger/history, completed idempotency response와 pending Outbox row를 하나의 PostgreSQL transaction에서 commit한다. Client response는 이 commit을 기다리지만 downstream consumer 완료는 기다리지 않는다.

전달 보장은 **at least once**다. Worker는 `FOR UPDATE SKIP LOCKED`로 처리 가능한 row를 lease한다. Processing lease가 만료되면 pending으로 돌아가 다른 Worker가 복구할 수 있다.

## 소비자 멱등 처리

각 Outbox row에는 API response나 player snapshot 대신 version이 포함된 event별 payload를 저장한다. `PostgresOperationalEventHandler`는 `(consumer_name, event_id)` primary key로 작은 운영 projection을 기록한다. 반복 전달은 `ON CONFLICT DO NOTHING`으로 중복 effect를 방지한다. Rolling deployment 중에는 이전 response 형태의 payload도 읽어 schema version `0`으로 projection한다.

Crash window는 다음 순서로 복구한다.

1. Consumer projection이 commit된다.
2. Worker가 Outbox row를 published로 바꾸기 전에 종료된다.
3. Lease 만료 후 event가 다시 전달된다.
4. Consumer는 같은 key를 확인해 effect를 반복하지 않고 Worker는 row를 published로 변경한다.

Projection에는 content version, reward count, ledger version 같은 event별 운영 field만 저장한다. 전체 command response, idempotency key, wallet 내용과 예상하지 못한 payload property는 복사하지 않는다. 지원하지 않는 event type과 payload/schema 불일치는 실패로 처리한다.

현재 PostgreSQL projection은 별도 broker 없이 delivery 경계를 실행 가능하게 검증하는 consumer다. Notification, analytics 또는 stream adapter로 교체해도 event ID 기반 멱등 contract를 유지한다.

## 재시도와 격리 처리

Handler 오류는 bounded exponential retry를 사용한다. DB에는 exception message 대신 안정적인 오류 코드를 저장한다.

- `outbox_event_unsupported`
- `outbox_payload_invalid`
- `outbox_consumer_store_unavailable`
- `outbox_consumer_failed`

`max_attempts` 이후 row는 `dead_letter`로 이동하고 `dead_lettered_at`을 기록한다. 이후 자동 lease 대상에서 제외한다.

Supervisor는 감사 대상 Admin API로만 dead-letter event를 replay할 수 있다. Replay는 사유 입력을 요구하며 attempts를 초기화하고 `manual_replay_count`를 증가시킨 뒤 row를 pending으로 돌린다. Viewer API는 count와 dead-letter metadata만 제공하며 payload와 idempotency key를 노출하지 않는다.

## 보존 정책

`PersistenceCleanupWorker`는 시작 시 한 번, 이후 제한된 주기로 실행된다. 기본 retention은 published Outbox 7일, orphan delivery projection 30일이다. Batch 크기, cycle당 최대 batch와 query timeout을 제한한다. Idempotency 정리 정책은 `docs/persistence/RETENTION.md`에 정의한다.

Published Outbox row와 consumer delivery projection은 하나의 PostgreSQL statement로 삭제한다. `pending`, `processing`, `dead_letter` row는 자동 삭제하지 않는다. Source Outbox row가 없고 별도 retention이 지난 projection만 orphan으로 정리한다.

Hard-kill integration test는 별도 consumer process의 projection commit을 기다린 뒤 Outbox status 갱신 전에 process를 종료한다. Lease 복구가 두 번째 projection 없이 event를 published로 만드는지 검증한다.

운영 scenario test는 정상 versioned event와 지원하지 않는 payload version event를 삽입한다. Worker가 정상 event를 publish하고 잘못된 event를 retry 후 `dead_letter`로 이동시키는지, 관련 delta metric과 Kibana rule이 생성되는지 확인한다.

## 운영 API

| Route | 최소 역할 | 용도 |
|---|---|---|
| `GET /api/admin/outbox/overview` | Viewer | Backlog, delivery, dead-letter count |
| `GET /api/admin/outbox/dead-letters` | Viewer | 제한된 terminal failure 목록 |
| `POST /api/admin/outbox/dead-letters/{eventId}/replay` | Supervisor | 사유와 audit가 필요한 replay |

Blazor `/outbox` 화면이 이 API를 사용한다. Replay는 `admin_operation_audit`에 `outbox.dead_letter.replay`로 기록한다.

## 관측 지표

Worker는 `Astra.LiveOps` trace와 다음 low-cardinality metric을 전송한다.

- `astra.outbox.leased`
- `astra.outbox.published`
- `astra.outbox.retry_scheduled`
- `astra.outbox.dead_lettered`
- `astra.outbox.cycle_failures`
- `astra.outbox.processing.duration`
- `astra.persistence.cleanup.published_outbox_deleted`
- `astra.persistence.cleanup.deliveries_deleted`
- `astra.persistence.cleanup.idempotency_deleted`
- `astra.persistence.cleanup.failures`

Metric tag는 event type과 outcome으로 제한한다. Event ID와 aggregate ID는 trace/log field에만 기록한다.

## 쓰기 증폭 벤치마크

`tests/Astra.IntegrationTests/WriteAmplificationBenchmark.cs`는 재화 지급 명령 하나가 기록하는 행 수가 플레이어 보유 자산 수에 비례하지 않는지 측정한다. `SaveStateAsync`가 변경분만 기록하는지 확인하는 회귀 가드이기도 하다.

`ASTRA_RUN_BENCHMARK=1`이 없으면 건너뛴다. 일반 테스트 실행에는 영향이 없다.

```powershell
docker compose -f deploy/docker-compose.yml --profile core up -d postgres
$env:ASTRA_RUN_BENCHMARK = '1'
$env:ASTRA_POSTGRES_CONNECTION = 'Host=localhost;Port=54329;Database=astra_bench;Username=astra;Password=astra_dev_password'
dotnet test tests/Astra.IntegrationTests/Astra.IntegrationTests.csproj -c Release `
    --filter WriteAmplificationBenchmark
```

행 기록 수는 `pg_stat_user_tables`의 `n_tup_ins`와 `n_tup_upd`를 각 구간 앞뒤로 읽어 차분한다. `pg_stat_reset()`을 쓰지 않으므로 superuser 권한이 필요 없다. 다른 세션이 같은 데이터베이스에 쓰면 수치가 오염되므로 전용 데이터베이스를 쓴다.

측정값은 실행 환경에 따라 달라진다. 절대 지연이 아니라 **명령당 행 수가 보유 자산 수와 무관하게 유지되는지**가 판정 대상이다.
