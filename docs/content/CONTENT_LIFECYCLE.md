# 콘텐츠 생명주기

## 데이터 소유권

- Multi-Silo 환경의 source of truth는 PostgreSQL이다.
- `content_snapshots`는 checksum이 포함된 immutable version을 저장한다.
- `active_content`는 단일 활성 version과 단조 증가 generation을 저장한다.
- 각 Silo는 process-local `ActiveContentCache` 하나를 가지며 game command는 이 cache만 조회한다.
- `InMemoryContentSnapshotStore`는 단일 Silo 개발과 테스트에만 사용한다.

## 배포

1. `EventConfigGrain`이 command를 검증하고 정규화한다.
2. 하나의 PostgreSQL transaction이 immutable snapshot을 삽입하고 `active_content`를 갱신한다.
3. 같은 transaction이 `pg_notify`를 호출하며 PostgreSQL은 commit 이후 notification을 전달한다.
4. 배포를 처리한 Silo가 응답 전에 local cache를 갱신한다.
5. 다른 Silo는 notification을 받고 commit된 active snapshot을 다시 읽는다.

현재 활성 version과 같은 checksum을 다시 배포하면 generation을 올리거나 notification을 보내지 않는다. 기존 비활성 version을 publish하면 `content.version.inactive`, 같은 version에 다른 checksum을 사용하면 `content.version.conflict`로 거부한다. 과거 version 재활성화는 rollback API를 사용한다.

## 롤백

Rollback은 과거 콘텐츠를 수정하거나 삭제하지 않는다. 하나의 transaction에서 `active_content`를 기존 immutable version으로 이동하고 generation을 증가시킨 뒤 cache notification을 전송한다.

## 장애 처리

- PostgreSQL advisory lock으로 SQL 적용을 직렬화하고 checksum과 함께 `astra_schema_migrations`에 기록한다. 적용된 파일은 변경하지 않고 새 번호의 migration을 추가한다.
- Transaction rollback은 notification과 active pointer 변경을 남기지 않는다.
- 누락된 notification은 기본 30초 간격 reconciliation이 복구한다.
- Listener 연결이 끊기면 마지막 유효 snapshot을 유지하고 bounded backoff로 재연결한다.
- PostgreSQL mode의 Silo는 Orleans cluster 참여와 gateway open 전에 active snapshot을 읽는다.
- 새 Silo는 PostgreSQL을 사용할 수 없는 동안 콘텐츠 의존 command를 처리하지 않는다.

LISTEN/NOTIFY는 cache 수렴을 빠르게 하지만 모든 Silo의 선형화된 동시 전환을 보장하지 않는다. 예약 콘텐츠는 gameplay 시작 전에 배포한다. 긴급 rollback의 전파 시간은 notification 전달 또는 reconciliation 간격으로 제한된다. Zero-window 전환이 필요하면 Silo별 acknowledgement barrier를 별도로 구성해야 한다.

운영 배포에서는 단일 pre-deployment job이 schema를 적용하고 일반 Silo replica는 `Astra:ApplyDatabaseSchema=false`를 사용한다. Embedded initializer는 로컬 개발과 CI 검증에서만 활성화한다.
