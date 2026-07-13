# ASTRA 통합 데모 결과

- 실행 ID: 20260713003438-3372c6
- 완료 시각(UTC): 2026-07-13T00:34:39.4987932+00:00
- 활성 콘텐츠: integrated-demo-20260713003438-3372c6
- 플레이어: 677aa59b-4023-4dc5-98b8-4d2f6e290cc9

| 검증 항목 | 결과 |
|---|---:|
| 가챠 재시도 실제 실행 1회 | PASS |
| 동일 가챠 응답 replay | PASS |
| 사고 대상 snapshot | PASS |
| 우편 재시도 실제 지급 1회 | PASS |
| 최종 엘리프 잔액 (500 - 100 + 200) | 600 |
| 필수 감사 작업 | PASS |
| Outbox 발행 증가량 | 6 |
| HTTP/TCP 교차 transport replay | PASS |

실행 주소: [Admin](http://127.0.0.1:5500) | [API 준비 상태](http://127.0.0.1:5191/health/ready)