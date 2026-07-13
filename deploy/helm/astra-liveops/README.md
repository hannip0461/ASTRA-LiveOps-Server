# ASTRA Helm 배포

이 chart는 API, Orleans Silo, TCP Gateway, Outbox Worker와 Blazor Admin을 배포한다. PostgreSQL은 source of truth이며 Redis는 선택적 가속 계층이다. Pre-install/pre-upgrade Job이 workload rollout 전에 versioned PostgreSQL schema를 적용한다.

값을 commit하지 않고 chart가 참조하는 Secret을 생성한다.

```powershell
kubectl -n astra create secret generic astra-liveops-secrets `
  --from-literal=postgres-connection='<Npgsql connection string>' `
  --from-literal=liveops-signing-key='<at least 32 bytes>' `
  --from-literal=tcp-signing-key='<at least 32 bytes>'
```

로컬 render와 검증 명령은 다음과 같다.

```powershell
helm lint deploy/helm/astra-liveops
helm template astra deploy/helm/astra-liveops --namespace astra
```

Chart는 database, Redis, public DNS, TLS와 Secret을 생성하지 않는다. 해당 resource는 platform이 소유한다.

TCP Gateway는 plaintext local listener를 사용하므로 내부 `ClusterIP`가 기본값이다. 외부 노출 시 TLS를 지원하는 신뢰된 L4 proxy 또는 load balancer를 사용한다.

운영 OIDC에서는 API token 검증과 Admin BFF client를 함께 설정한다.

```yaml
global:
  liveOpsAuth:
    authority: https://identity.example.com/tenant
    audience: astra-liveops-admin-api
    roleClaimType: roles
components:
  admin:
    replicas: 1
    auth:
      openIdConnect:
        enabled: true
        authority: https://identity.example.com/tenant
        clientId: astra-admin
        publicOrigin: https://admin.example.com
        apiScope: astra-liveops-admin-api/.default
        roleClaimType: roles
```

기존 Secret에 `admin-oidc-client-secret`을 추가한다. 이 mode에서 API는 `liveops-signing-key`를 사용하지 않는다. Provider는 안정적인 `sub`, identity token의 Admin role claim과 API access token의 동일한 LiveOps role을 발급해야 한다.
