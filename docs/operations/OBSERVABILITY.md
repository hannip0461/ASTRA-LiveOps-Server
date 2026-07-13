# 관측성 구성

## Telemetry 흐름

```text
Astra.Api / Astra.TcpGateway / Astra.Silo / Astra.Worker
  -> OTLP/gRPC :4317
  -> EDOT Collector gateway
  -> OTel-native Elasticsearch data streams
  -> Kibana dashboard and alert rules
```

Self-managed 환경은 SDK data를 legacy APM Server endpoint에 직접 전송하지 않고 Elastic EDOT Collector를 사용한다. `elasticapm` processor와 connector가 trace를 보강하고 Elastic APM 화면에 필요한 metric을 생성한다. Elasticsearch exporter는 `mapping.mode: otel`로 OTel field name을 유지한다.

Application Counter는 delta temporality를 사용한다. Alert window는 process lifetime 누적값이 아니라 해당 구간에 새로 발생한 오류를 평가한다.

## 로컬 자원 제한

Observability stack은 별도 profile로 분리하며 PostgreSQL core profile과 함께 자동 시작하지 않는다.

- Elastic Stack image version: `9.4.2`
- Elasticsearch telemetry: 1 GiB `tmpfs`
- Kibana local data: 128 MiB `tmpfs`
- Memory limit: Elasticsearch 1280 MiB, Kibana 1024 MiB, EDOT 384 MiB
- Container log: 10 MiB 파일 3개 rotation
- Data stream retention: 기본 1시간, 최대 2시간
- Rollover: 15분 또는 64 MiB

로컬 Elasticsearch/Kibana는 loopback port에서만 security를 비활성화한다. 운영 환경은 TLS, API key 인증, 지속 가능한 capacity와 별도 retention 정책을 적용해야 한다.

## 실행과 검증

```powershell
./scripts/observability/Start-Observability.ps1
./scripts/observability/Test-Observability.ps1
./scripts/observability/Test-OperationalScenarios.ps1
```

`Test-Observability.ps1`은 container health check를 넘어 다음 E2E를 검증한다.

1. `Astra.ObservabilityProbe`가 실제 OTLP trace와 metric을 전송한다.
2. ES|QL로 OTel-native data stream 2개를 확인한다.
3. 제한된 lifecycle 설정을 적용한다.
4. Kibana Dashboards API로 ASTRA dashboard를 생성한다.
5. 안정적인 rule ID로 PostgreSQL pool-timeout rule을 생성한다.
6. 구분 가능한 synthetic timeout metric을 한 건 전송한다.
7. Kibana active alert와 dashboard ID를 확인한다.

`Test-OperationalScenarios.ps1`은 실제 장애 시나리오 2개를 실행한다.

1. Connection 2개인 PostgreSQL pool을 포화시켜 한 번의 획득 timeout과 connection 반환 후 query 회복을 검증한다.
2. 정상 Outbox event와 잘못된 event를 Worker가 처리하도록 해 `published`, bounded retry, `dead_letter`와 alert를 검증한다.

Script는 delta `attempts`, `failures`로 99.9% acquisition SLO burn rate를 계산하고 `output/evidence/operational-scenarios.json`에 결과를 저장한다. Probe ID를 OTel `service.instance.id`로 사용해 이전 telemetry가 결과에 섞이지 않게 한다.

Application service의 OTLP endpoint는 다음과 같이 지정한다.

```powershell
$env:Astra__OpenTelemetry__OtlpEndpoint='http://127.0.0.1:4317'
```

Kibana 주소는 `http://127.0.0.1:5609`이며 test script가 dashboard URL을 출력한다.

## 종료

```powershell
./scripts/observability/Stop-Observability.ps1
```

Stop script는 observability container 3개와 ephemeral mount만 제거한다. PostgreSQL, named volume과 image layer는 변경하지 않는다.
