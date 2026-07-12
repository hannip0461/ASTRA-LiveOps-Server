# ASTRA Helm chart

The chart deploys the API, Orleans silos, TCP gateway, Outbox worker, and Blazor Admin. PostgreSQL is the source of truth; Redis is optional. A pre-install/pre-upgrade Job applies the versioned PostgreSQL schema before workloads roll out.

Create the referenced Secret without committing values:

```powershell
kubectl -n astra create secret generic astra-liveops-secrets `
  --from-literal=postgres-connection='<Npgsql connection string>' `
  --from-literal=liveops-signing-key='<at least 32 bytes>' `
  --from-literal=tcp-signing-key='<at least 32 bytes>'
```

Render and validate locally:

```powershell
helm lint deploy/helm/astra-liveops
helm template astra deploy/helm/astra-liveops --namespace astra
```

The chart does not create databases, Redis, public DNS, TLS, or secrets. Those remain platform-owned resources.

The TCP Gateway defaults to an internal `ClusterIP` because its local listener is plaintext. Expose it only through a trusted TLS-capable L4 proxy or load balancer; changing the Service to `LoadBalancer` without TLS termination is not a production configuration.

For production OIDC, configure both API token validation and the Admin BFF client:

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

Add `admin-oidc-client-secret` to the existing Secret. In this mode `liveops-signing-key` is not consumed by the API. The provider must issue a stable `sub`, an Admin role claim in the identity token, and the same LiveOps role in the API access token.
