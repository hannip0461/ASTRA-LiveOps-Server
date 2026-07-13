# 영속 데이터 보존 정책

## 멱등성 데이터 생명주기

성공한 player command는 completed response envelope와 snapshot을 24시간 만료 시각과 함께 저장한다. Request-level `PENDING` row는 사용하지 않는다. 만료 전 재시도는 저장된 응답을 반환하고 만료 후 요청은 새 command로 처리한다.

두 cleanup 경로가 같은 만료 기준을 사용한다.

1. Account command는 player lock 획득 후 해당 player의 만료 row를 삭제한다.
2. `PersistenceCleanupWorker`는 더 이상 traffic이 없는 account의 만료 row를 삭제한다.

Global cleanup은 만료 후 기본 1시간 grace를 적용한다. Grace는 운영 cleanup 지연을 위한 값이며 replay 가능 시간을 연장하지 않는다.

## DB 제한

Cleanup query는 `(expires_at, player_id, idempotency_key)` index와 같은 순서로 candidate를 선택하고 `FOR UPDATE SKIP LOCKED`를 사용한다. 여러 Worker는 서로 기다리지 않고 다른 batch를 처리한다.

| 설정 | 기본값 |
|---|---:|
| Cleanup interval | 1시간 |
| Table별 batch row | 500 |
| Cycle당 최대 batch | 20 |
| SQL command timeout | 5초 |
| 만료 idempotency grace | 1시간 |

시작 검증은 batch 10,000 row, cycle 100 batch, command timeout 30초를 상한으로 제한한다. Timeout은 statement를 rollback하고 `astra.persistence.cleanup.failures`를 기록한다. 제한 없는 delete로 전환하지 않는다.

같은 PostgreSQL statement가 published Outbox, orphan delivery와 expired idempotency를 제한된 batch로 정리한다. `pending`, `processing`, `dead_letter` Outbox row는 제외한다.

## 디스크 동작

PostgreSQL `DELETE`는 autovacuum 이후 dead tuple 공간을 재사용하지만 Docker volume file을 즉시 축소하지 않는다. Application은 table rewrite lock이 필요한 `VACUUM FULL`을 실행하지 않는다. 물리 file compaction은 별도 maintenance window에서 수행한다.

Ledger, gacha history, mail claim과 admin audit는 이 cleanup 대상이 아니다. 해당 데이터는 idempotency TTL과 분리된 product/legal retention 정책을 적용해야 한다.
