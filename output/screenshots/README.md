# 운영 화면

- Admin viewport: `1440 x 1000`
- Mobile QA viewport: `390 x 844`

| 파일 | 확인 내용 |
|---|---|
| `01-content-ops.png` | Immutable content version, checksum과 active snapshot 배포 |
| `02-incident-mail.png` | 사고 대상 확인, immutable mail definition과 멱등 claim 입력 |
| `03-audit-log.png` | 콘텐츠·재화·가챠·사고·우편 작업의 인증된 actor 기록 |
| `04-outbox-operations.png` | Outbox backlog, 발행 event, retry와 의도적으로 주입한 dead-letter |
| `astra-operational-dashboard.png` | API, TCP, Grain, PostgreSQL, Outbox와 burn-rate telemetry |
| `astra-observability-dashboard.png` | OpenTelemetry 및 Elastic APM 기본 dashboard |

## 화면 검증

- Content, Incident/Mail, Audit, Outbox 화면의 browser console error와 warning 0건
- Desktop 4개 화면의 horizontal overflow 없음
- 390 px mobile Content 화면의 horizontal overflow와 비정상 overlap 없음
- Screenshot에 access token과 signing key 미포함

`outbox_payload_invalid`는 dead-letter 진단 흐름을 검증하기 위해 의도적으로 주입한 오류다.
