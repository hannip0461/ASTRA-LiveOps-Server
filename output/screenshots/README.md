# 운영 화면

- 운영 도구 화면 크기: `1440 x 1000`
- 모바일 검수 화면 크기: `390 x 844`

| 파일 | 확인 내용 |
|---|---|
| `01-content-ops.png` | 불변 콘텐츠 버전, 체크섬과 활성 스냅샷 배포 |
| `02-incident-mail.png` | 사고 대상 확인, 불변 우편 정의와 멱등 수령 입력 |
| `03-audit-log.png` | 콘텐츠, 재화, 가챠, 사고, 우편 작업의 인증된 작업자 기록 |
| `04-outbox-operations.png` | Outbox 적체, 발행 이벤트, 재시도와 의도적으로 주입한 최종 실패 |
| `astra-operational-dashboard.png` | API, TCP, Grain, PostgreSQL, Outbox와 오류 예산 소진율 관측 정보 |
| `astra-observability-dashboard.png` | OpenTelemetry 및 Elastic APM 기본 대시보드 |

## 화면 검증

- 콘텐츠, 사고 보상, 감사 로그, Outbox 화면의 브라우저 콘솔 오류와 경고 0건
- 데스크톱 4개 화면에서 가로 넘침 없음
- 390px 모바일 콘텐츠 화면에서 가로 넘침과 비정상적인 겹침 없음
- 화면 이미지에 액세스 토큰과 서명 키 미포함

`outbox_payload_invalid`는 dead-letter 진단 흐름을 검증하기 위해 의도적으로 주입한 오류다.
