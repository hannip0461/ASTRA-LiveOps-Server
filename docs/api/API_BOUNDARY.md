# API 경계

## 오류 응답

본문이 없는 HTTP 오류와 처리된 예외는 `application/problem+json`으로 응답한다.

- `status`: HTTP 상태 코드
- `code`: 안정적인 ASTRA 오류 코드
- `traceId`: 분산 trace 상관관계 값
- `type`: `urn:astra:problem:{code}`
- `errors`: 입력 검증 실패에만 포함하는 field별 메시지

인증과 권한 오류는 각각 `authentication_required`, `permission_denied`를 사용한다. Domain 오류는 예외 메시지나 stack trace를 노출하지 않고 `idempotency_conflict`, `insufficient_currency`, `content_mismatch`, `mail_already_claimed` 같은 코드로 변환한다. 예상하지 못한 오류는 `internal_error`, Orleans 또는 PostgreSQL 장애는 `dependency_unavailable`로 응답한다.

## 입력 검증

HTTP 입력은 audit intent 또는 Grain 호출 전에 검증하고 정규화한다. Null nested content, 지원하지 않는 enum, 빈 GUID, 허용되지 않은 식별자, 과도한 collection, 잘못된 시간 범위와 운영 한계를 벗어난 숫자를 거부한다.

| 입력 | 제한 |
|---|---|
| Request body | 1 MiB |
| 식별자 / idempotency key | ASCII 식별 문자 128자 |
| 운영 사유 | 500자 |
| 재화 수량 | 1 ~ 1,000,000,000,000 |
| 콘텐츠 banner | 1 ~ 100개 |
| Banner별 보상 | 1 ~ 500개 |
| 우편 대상자 | 1 ~ 10,000명 |
| 우편 보상 | 1 ~ 20개 |
| Audit page 크기 | 1 ~ 200개 |

서버는 정규화한 입력으로 request hash를 다시 생성하며 client가 전달한 hash를 신뢰하지 않는다.

## 요청 제한

Fixed-window rate limit은 인증된 actor ID별로 분리한다. Token을 갱신해도 동일 actor의 partition을 유지한다.

| API 영역 | Viewer | Operator | Supervisor |
|---|---:|---:|---:|
| 조회 요청/분 | 120 | 180 | 240 |
| 변경 요청/분 | 해당 없음 | 30 | 60 |

개발용 token 발급은 source address별 분당 20회로 제한한다. 제한을 초과하면 `429 rate_limited`와 `Retry-After` header를 반환한다. 설정은 `Astra:RateLimits`에 있으며 시작 시 유효성을 검사한다.

## OpenAPI 문서

`Astra.Api`는 Development 환경에서 `GET /openapi/v1.json`으로 OpenAPI 3.1 문서를 제공한다. 저장소 최상위 `openapi.json`은 그 응답을 그대로 저장한 산출물이며 엔드포인트 19개와 스키마 14개를 담는다. 계약 변경을 diff로 검토하려면 아래로 재생성한다.

```powershell
# Astra.Silo와 Astra.Api를 Development로 실행한 뒤
Invoke-WebRequest http://localhost:5191/openapi/v1.json |
    Select-Object -ExpandProperty Content |
    Set-Content openapi.json -Encoding utf8 -NoNewline
```

`servers` 항목은 문서를 받아온 호스트를 그대로 반영하므로 로컬 실행 주소가 기록된다. 배포 주소를 명시하려면 `Astra.Api`에서 OpenAPI 문서 변환기를 추가해야 한다.
