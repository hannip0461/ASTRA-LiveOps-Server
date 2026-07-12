# Deployment

## Ownership

| Layer | Owner | Implemented artifact |
|---|---|---|
| Local dependencies and observability | Docker Compose | `deploy/docker-compose.yml` |
| Service images | Docker BuildKit | root `Dockerfile`, five final targets |
| Kubernetes application | Helm | `deploy/helm/astra-liveops` |
| Azure foundation | Terraform | `deploy/terraform` |
| Application release | Pulumi | `deploy/pulumi` |
| Validation | GitHub Actions | `.github/workflows/ci.yml` |

Terraform and Pulumi deliberately do not own the same resources. Terraform creates Azure networking, private AKS, ACR, Log Analytics, and private PostgreSQL. Pulumi receives an existing kubeconfig and installs the Helm release.

## Orleans cluster path

Local development defaults to `Astra:Orleans:ClusterProvider=Localhost`. Kubernetes sets it to `AdoNet`; API and TCP clients discover gateways through PostgreSQL, and each Silo advertises its Pod IP. `ClusterId` and `ServiceId` must match across all three processes.

The versioned SQL migrations include the Microsoft Orleans 10.2.1 PostgreSQL base schema and 3.7 clustering migration. Helm runs `Astra.Worker --migrate-only` as a pre-install/pre-upgrade Job. Ordinary Silo replicas keep schema application disabled.

## Secret boundary

The chart references, but never creates, `astra-liveops-secrets`. Required keys are:

- `postgres-connection`
- `liveops-signing-key` when the API uses local HMAC trust
- `admin-oidc-client-secret` when Admin OIDC is enabled
- `tcp-signing-key`
- `redis-connection` only when Redis acceleration is enabled

Set `global.liveOpsAuth.authority` to make the API validate externally issued access tokens without the local signing-key secret. Set `components.admin.auth.openIdConnect` for the Admin confidential code-flow client. `publicOrigin` is the externally trusted HTTPS origin used to build callback/logout URIs even when TLS terminates at an Ingress. OIDC mode requires one Admin replica because its BFF token store is pod-local.

The Admin Service is internal `ClusterIP`; no Ingress, public DNS, TLS certificate, identity-provider tenant/application, or callback registration is created by this repository. The platform must expose Admin through HTTPS and register `/signin-oidc` and `/signout-callback-oidc` with the provider.

The TCP Gateway also defaults to `ClusterIP`. Its application listener is plaintext, so the platform must terminate TLS at a trusted L4 proxy/load balancer before exposing it. A direct public `LoadBalancer` Service is intentionally not the chart default.

## Resource and disk bounds

- Every workload has CPU/memory requests and limits.
- Containers run non-root, drop Linux capabilities, and use a read-only root filesystem.
- Writable `/tmp` uses size-limited `emptyDir` volumes.
- Local Elasticsearch uses a 1 GiB tmpfs; Kibana uses a 128 MiB tmpfs.
- CI builds images on an ephemeral runner. Local checks use `docker build --check` unless an image is explicitly needed.
- No automated prune command is included.

## Verified locally

- Release build: 0 warnings, 0 errors
- Standard tests: 91 passed
- PostgreSQL-filtered tests: 17 passed
- ADO.NET cluster: two active Silo membership rows
- HTTP/TCP live E2E through ADO.NET membership: 4 passed
- Helm lint and render: 11 resources
- Terraform 4.80 provider validation: valid
- Pulumi C# deployment program: 0 warnings, 0 errors; offline preview 3 resources
- Docker BuildKit static check: no warnings

Pulumi offline preview does not require kubeconfig. Actual `up` requires a reachable cluster and secret workflow. Azure `plan/apply` remains outside local and pull-request validation because it requires credentials and can create billable resources.
