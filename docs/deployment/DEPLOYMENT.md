# 배포 구성

## 구성별 책임

| 영역 | 도구 | 구현 산출물 |
|---|---|---|
| 로컬 의존성과 관측성 | Docker Compose | `deploy/docker-compose.yml` |
| Service image | Docker BuildKit | Root `Dockerfile`의 final target 5개 |
| Kubernetes application | Helm | `deploy/helm/astra-liveops` |
| Azure 기반 자원 | Terraform | `deploy/terraform` |
| Application release | Pulumi | `deploy/pulumi` |
| 자동 검증 | GitHub Actions | `.github/workflows/ci.yml` |

Terraform과 Pulumi는 동일 resource를 함께 소유하지 않는다. Terraform은 Azure network, private AKS, ACR, Log Analytics와 private PostgreSQL을 생성한다. Pulumi는 기존 kubeconfig를 받아 Helm release만 설치한다.

## Orleans 클러스터 경로

로컬 개발은 `Astra:Orleans:ClusterProvider=Localhost`를 기본값으로 사용한다. Kubernetes는 `AdoNet`을 사용하며 API와 TCP client는 PostgreSQL에서 gateway를 조회한다. 각 Silo는 Pod IP를 advertise하며 세 process의 `ClusterId`와 `ServiceId`가 일치해야 한다.

Versioned SQL migration에는 Microsoft Orleans 10.2.1 PostgreSQL base schema와 3.7 clustering migration이 포함된다. Helm은 pre-install/pre-upgrade Job에서 `Astra.Worker --migrate-only`를 실행하며 일반 Silo replica의 schema 적용은 비활성화한다.

## 비밀값 경계

Chart는 `astra-liveops-secrets`를 참조하지만 생성하지 않는다.

- `postgres-connection`
- API가 local HMAC trust를 사용할 때 `liveops-signing-key`
- Admin OIDC를 사용할 때 `admin-oidc-client-secret`
- `tcp-signing-key`
- Redis acceleration을 사용할 때 `redis-connection`

`global.liveOpsAuth.authority`를 설정하면 API가 local signing key 없이 외부 access token을 검증한다. Admin confidential code flow는 `components.admin.auth.openIdConnect`에서 설정한다. `publicOrigin`은 Ingress에서 TLS가 종료되어도 callback/logout URI 생성에 사용하는 신뢰된 HTTPS origin이다. 현재 BFF token store가 pod-local이므로 OIDC mode의 Admin replica는 1개로 제한한다.

Admin Service는 내부 `ClusterIP`다. 이 저장소는 Ingress, public DNS, TLS certificate, identity provider tenant/application과 callback 등록을 만들지 않는다. Platform은 Admin을 HTTPS로 노출하고 `/signin-oidc`, `/signout-callback-oidc`를 provider에 등록해야 한다.

TCP Gateway도 기본적으로 `ClusterIP`를 사용한다. Application listener가 plaintext이므로 외부 노출 시 신뢰된 L4 proxy 또는 load balancer에서 TLS를 종료해야 한다.

## 자원 제한

- 모든 workload에 CPU/memory request와 limit을 지정한다.
- Container는 non-root로 실행하고 Linux capability를 제거하며 root filesystem을 read-only로 사용한다.
- Writable `/tmp`는 size limit이 있는 `emptyDir`를 사용한다.
- 로컬 Elasticsearch는 1 GiB, Kibana는 128 MiB tmpfs를 사용한다.
- CI image build는 ephemeral runner에서 수행한다.
- 자동 prune command는 포함하지 않는다.

## 검증 결과

- Release build: warning 0, error 0
- 표준 테스트: 91건 통과
- PostgreSQL 테스트: 17건 통과
- ADO.NET cluster: Active Silo membership row 2개
- ADO.NET membership 기반 HTTP/TCP E2E: 4건 통과
- Helm lint/render: resource 11개
- Terraform azurerm 4.80 validation: 통과
- Pulumi C# program: warning 0, error 0, offline preview resource 3개
- Docker image 5종 GHCR 발행

Pulumi offline preview는 kubeconfig 없이 실행할 수 있다. 실제 `pulumi up`에는 접근 가능한 cluster와 secret workflow가 필요하다. Azure `plan/apply`는 credential과 비용이 발생할 수 있어 로컬 및 pull request 검증 범위에서 제외한다.
