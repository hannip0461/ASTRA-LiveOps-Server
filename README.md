# ASTRA LiveOps Server

트릭컬 리바이브 같은 수집형 RPG의 LiveOps 문제를 기준으로 구현한 .NET 10 / Microsoft Orleans 서버.

## 검토 순서

1. `docs/portfolio/ASTRA_LiveOps_Portfolio.pdf`에서 문제 정의와 검증 결과를 확인한다.
2. 아래 `Core Scope`와 `Current Implementation`에서 코드 범위를 확인한다.
3. `pwsh -File scripts/demo/Run-PortfolioDemo.ps1`로 핵심 시나리오를 재현한다.
4. 실행 결과는 `output/demo/portfolio-demo-summary.md`, 화면 증빙은 `output/playwright/README.md`에서 확인한다.

## Core Scope

- Gacha, pity, wallet, inventory consistency
- Idempotency without request PENDING rows
- Incident impact snapshot and targeted compensation mail
- Blazor LiveOps Admin
- TCP + Protobuf gateway calling Orleans directly
- PostgreSQL source of truth, Redis acceleration

## Solution Layout

```text
src/
  Astra.Api             HTTP API
  Astra.TcpGateway      TCP + Protobuf gateway
  Astra.Silo            Orleans silo host
  Astra.Admin           Blazor LiveOps admin
  Astra.Worker          Outbox and operations worker
  Astra.Contracts       Shared contracts
  Astra.Domain          Domain model and command rules
  Astra.Infrastructure  PostgreSQL, Redis, persistence adapters
tests/
  Astra.UnitTests
  Astra.IntegrationTests
  Astra.ConcurrencyTests
  Astra.FailureTests
deploy/
  docker-compose.yml
  helm/astra-liveops
  terraform
  pulumi
Dockerfile              Multi-target production images
.github/workflows/ci.yml
```

## Local Build

```powershell
dotnet build Astra.LiveOps.slnx
```

Docker Compose services are profile-gated. Start only the profile needed for a test; observability data is tmpfs-bounded.

## Portfolio Demo

Run the complete gacha-idempotency, incident-compensation, audit/Outbox, and HTTP/TCP replay demonstration with one command:

```powershell
pwsh -File scripts/demo/Run-PortfolioDemo.ps1
```

The latest bounded evidence is written to `output/demo`. Runtime and disk behavior are documented in `docs/demo/PORTFOLIO_DEMO.md`.

## Current Implementation

- PlayerAccountGrain contract and implementation
- Wallet grant/spend command processor
- Idempotency replay without request PENDING rows
- Exact response-envelope replay within TTL and lazy cleanup before key reuse
- In-memory transaction store for early tests
- PostgreSQL transaction store and schema
- Immutable PostgreSQL content snapshots and atomic active-version publish/rollback
- Silo-local ActiveContentCache synchronized by PostgreSQL LISTEN/NOTIFY with periodic reconciliation
- Gacha draw requests use active content snapshot for version, checksum, cost, and weighted reward pool
- Pity guarantee/reset, duplicate character conversion, inventory update, and gacha history in one transaction
- Incident mail target snapshot and idempotent mail claim
- Redis target membership cache for incident mail eligibility
- Redis command/connection failure fallback to PostgreSQL
- Blazor admin page for incident mail create/check/claim replay demo
- TCP + Protobuf v1 framing, signed session binding, and direct Orleans Grain calls
- HTTP/TCP cross-transport replay using the same server-generated request hash and idempotency key
- JWT Bearer LiveOps RBAC with local HMAC development tokens or external OIDC authority validation
- Blazor Admin OIDC code flow + PKCE BFF boundary with server-side API access-token sessions
- PostgreSQL operation audit with authenticated actor, intent-first lifecycle, and Blazor review page
- RFC 7807-style ProblemDetails, bounded input validation, and actor-partitioned role-aware rate limits
- Transactional outbox events and concurrent PostgreSQL lease/retry worker
- Idempotent operational consumer with sanitized projections, terminal dead-letter state, and audited Supervisor replay
- Bounded retention cleanup that atomically removes expired published events and their delivery projections
- Global expired-idempotency cleanup with grace period, `SKIP LOCKED` batching, and a bounded query timeout
- Per-service PostgreSQL pool budgets with bounded wait/command timeouts and saturation recovery tests
- Blazor outbox backlog/dead-letter operations page without payload or idempotency-key exposure
- OTel HTTP/TCP/PostgreSQL traces, native Npgsql pool metrics, and bounded connection-acquisition metrics
- Reproducible PostgreSQL saturation burn-rate and Outbox published/retry/dead-letter fault evidence
- TCP delivery-complete request count/latency metrics with low-cardinality outcomes
- Profile-gated EDOT -> Elasticsearch -> Kibana pipeline with Dashboard as Code and an executable alert probe
- Configurable OpenTelemetry console/OTLP exporters for API, Silo, and Worker
- Worker E2E verification from PostgreSQL outbox event to published status
- OS hard-kill recovery test for the consumer-commit/outbox-publish crash window
- Concurrency and failure tests for wallet/idempotency behavior
- Failure injection proving a post-debit gacha error leaves no partial state
- GitHub Actions build/test with a real PostgreSQL service
- PostgreSQL-backed Orleans ADO.NET membership with pinned upstream schema migrations
- Two-Silo ADO.NET membership and HTTP/TCP Grain RPC verification
- Multi-target non-root Docker images for API, Admin, Silo, TCP Gateway, and Worker
- Helm chart with bounded resources, probes, Secret references, PDB, and migration hook
- Terraform Azure foundation for private AKS, ACR, VNet, Log Analytics, and private PostgreSQL
- Pulumi C# program that owns only the Helm application release
- GitHub Actions deployment validation for Docker, Helm, Terraform, and Pulumi

`InMemory` storage is for single-Silo development and unit tests only. PostgreSQL mode is the multi-Silo runtime path.
Content lifecycle and failure behavior are documented in `docs/content/CONTENT_LIFECYCLE.md`.
LiveOps authentication, route permissions, and audit semantics are documented in `docs/security/LIVEOPS_AUTH_AUDIT.md`.
HTTP error, validation, and request-limit contracts are documented in `docs/api/API_BOUNDARY.md`.
Outbox delivery, crash recovery, dead-letter, and replay semantics are documented in `docs/operations/OUTBOX_DELIVERY.md`.
Persistence TTL and database-load guardrails are documented in `docs/persistence/RETENTION.md`.
PostgreSQL pool budgets, SLOs, and alert thresholds are documented in `docs/operations/POSTGRES_POOL_SLO.md`.
The bounded Elastic/EDOT runtime and its end-to-end verification are documented in `docs/operations/OBSERVABILITY.md`.
Container, Kubernetes, Terraform, Pulumi, and deployment ownership are documented in `docs/deployment/DEPLOYMENT.md`.

## Deployment Validation

```powershell
docker build --check --target api .
helm lint deploy/helm/astra-liveops
helm template astra deploy/helm/astra-liveops --namespace astra
terraform -chdir=deploy/terraform init -backend=false
terraform -chdir=deploy/terraform validate
dotnet build deploy/pulumi/Astra.LiveOps.Deploy.csproj -c Release
```

CI builds all five Docker targets. Local verification uses `docker build --check` by default to avoid unnecessary Docker Desktop disk growth.

## Optional PostgreSQL Verification

This pulls/runs only PostgreSQL. Do not enable the observability profile during early work.

```powershell
docker compose -f deploy/docker-compose.yml --profile core up -d postgres
$env:ASTRA_RUN_POSTGRES_TESTS='1'
dotnet test tests/Astra.IntegrationTests/Astra.IntegrationTests.csproj --filter Postgres
```

Stop the container without deleting the named volume:

```powershell
docker compose -f deploy/docker-compose.yml stop postgres
```

## TCP Gateway E2E

Start `Astra.Silo`, `Astra.Api`, and `Astra.TcpGateway`, then run:

```powershell
$env:ASTRA_RUN_TCP_E2E='1'
dotnet test tests/Astra.IntegrationTests/Astra.IntegrationTests.csproj --filter TcpGatewayEndToEndTests
```

The test publishes content over HTTP, seeds one player, draws through TCP, reconnects, replays the same idempotency key, retries an HTTP draw through TCP, and compares the TCP wallet with the HTTP wallet. Wire details are in `docs/tcp/PROTOCOL.md`.
