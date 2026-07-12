# LiveOps Authentication and Audit

## Authentication Boundary

`Astra.Api` supports two mutually exclusive JWT Bearer trust modes. Local development validates HMAC tokens using a fixed issuer, audience, algorithm, lifetime, and role claim. Production can instead set `Astra:LiveOpsAuth:Authority`; the API then obtains signing keys from the OIDC discovery metadata and validates the configured audience, lifetime, issuer, and configurable name/role claims. External-authority mode neither needs nor accepts local development token issuance.

Audit actor extraction uses the validated `ClaimsIdentity` name/role mapping rather than hard-coded claim names. Custom OIDC `NameClaimType` and `RoleClaimType` values therefore remain consistent across authorization and audit persistence.

The development token endpoint is mapped only when both conditions are true:

- the API environment is `Development`;
- `Astra:LiveOpsAuth:DevTokenEnabled=true`.

It additionally requires a 32-byte-or-longer bootstrap key supplied outside committed configuration. Requests must be direct loopback connections and are rejected when forwarding headers are present. It accepts only configured operator IDs and cannot choose or elevate a caller-supplied role. The development JWT signing key and bootstrap key are injected through `Astra__LiveOpsAuth__SigningKey` and `Astra__LiveOpsAuth__DevTokenKey`; neither has a committed development default.

`Astra.Admin` has its own cookie-authenticated BFF boundary. Development sign-in is loopback-only, antiforgery-protected, and exchanges a configured operator ID plus the bootstrap key for an API token. Production can enable confidential OIDC authorization-code flow with PKCE. The acquired API access token is removed from authentication properties and kept only in the pod-local session store; the browser receives a protected, HTTP-only cookie. The identity token may remain in the protected cookie only for standards-compliant OIDC sign-out.

OIDC requires an explicit public HTTPS origin. Redirect and post-logout URIs are built from that trusted value instead of request forwarding headers, preventing an internal HTTP service address or spoofed host from entering the authorization request after Ingress TLS termination.

Every Admin API client method checks the signed-in user's minimum role before forwarding that same user's access token, so the UI cannot act as a shared Supervisor deputy. OIDC must issue `sub`, the configured Admin role claim in the identity token, and the same LiveOps role claim in the API access token. Supported values are `LiveOpsViewer`, `LiveOpsOperator`, and `LiveOpsSupervisor`. Missing identity, role, access token, or expiry fails sign-in closed. Access-token expiry also expires the Admin cookie; no refresh token is retained.

The current BFF session store is intentionally pod-local, so Helm rejects OIDC Admin replica counts above one. Pod restart invalidates sessions and requires sign-in again. Shared Data Protection and a distributed encrypted token store are required before scaling this boundary horizontally. A shared operator token is intentionally unsupported.

## Role Matrix

| Route | Minimum role |
| --- | --- |
| `GET /api/admin/content/active` | Viewer |
| `GET /api/admin/mail/...` | Viewer |
| `GET /api/admin/audit` | Viewer |
| `POST /api/admin/content/publish` | Operator |
| `POST /api/admin/mail/incident` | Operator |
| `POST /api/admin/content/rollback/{version}` | Supervisor |
| `GET /api/players/{id}/wallet` | Viewer |
| HTTP player state mutations | Operator |

Supervisor satisfies Operator and Viewer policies; Operator satisfies Viewer. The HTTP player routes are an authenticated operations/simulator surface. The production game-client path is the signed TCP session protocol.

## Audit Lifecycle

For each authenticated state-changing HTTP operation:

1. The API inserts a `started` audit row before calling Orleans.
2. The row records actor ID, display name, role, trace correlation ID, action, target, reason, source address, and a sanitized request summary.
3. A domain success changes the row to `succeeded`.
4. A domain validation rejection changes it to `rejected` with a stable error code.
5. An exception changes it to `failed` with a stable API error code, without storing CLR type names or exception messages.

Tokens, request hashes, mail bodies, and complete compensation target lists are not stored. Target counts and reward summaries are sufficient for operations review.

The audit row and Grain mutation cannot share one database transaction because API and Silo are separate processes. Intent-first persistence is deliberate: a crash after the domain commit leaves `started`, which means the outcome is uncertain and must be reconciled using the target and trace ID. It does not silently lose the operator action.

Audit completion permits one transition from `started`; later updates are rejected. Recent entries are available in the Blazor `Audit Log` view and through the Viewer-authorized API.
