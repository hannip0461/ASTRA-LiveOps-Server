# 통합 데모

## 실행

Docker Desktop이 실행 중이어야 한다. Script는 PostgreSQL만 시작하고 Release binary를 build한 뒤 현재 저장소의 ASTRA process를 재시작해 두 가지 핵심 시나리오를 실행한다.

```powershell
pwsh -File scripts/demo/Run-IntegratedDemo.ps1
```

기존 Release build를 재사용하려면 다음과 같이 실행한다.

```powershell
pwsh -File scripts/demo/Run-IntegratedDemo.ps1 -SkipBuild
```

`-SkipTcpVerification`은 HTTP/TCP 교차 transport E2E만 생략한다. 가챠 정합성과 사고 보상 검증은 계속 실행한다.

## 실행 증빙

각 실행은 다음 파일을 최신 결과로 교체한다.

- `output/demo/integrated-demo-evidence.json`
- `output/demo/integrated-demo-summary.md`
- `output/demo/integrated-demo-tcp-e2e.log`

증빙에는 access token과 signing key를 저장하지 않는다. 활성 콘텐츠 checksum, idempotency replay, 최종 재화 계산, 사고 대상 일치, audit coverage, Outbox 발행 증가량과 TCP 검증 결과를 기록한다.

운영 화면과 QA 결과는 `output/screenshots/README.md`에서 확인한다.

## 종료

ASTRA application process 5개만 종료한다.

```powershell
pwsh -File scripts/demo/Stop-IntegratedDemo.ps1
```

Volume을 유지하면서 PostgreSQL도 종료하려면 다음과 같이 실행한다.

```powershell
pwsh -File scripts/demo/Stop-IntegratedDemo.ps1 -StopPostgres
```

Script는 Docker prune, volume 삭제, Redis 시작과 observability profile 시작을 수행하지 않는다. 이미 실행 중인 observability container도 변경하지 않는다.
