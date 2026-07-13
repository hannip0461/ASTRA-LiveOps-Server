# LiveOps 인증과 감사

## 인증 경계

`Astra.Api`는 상호 배타적인 JWT Bearer trust mode 2개를 지원한다. 로컬 개발은 고정 issuer, audience, algorithm, lifetime과 role claim으로 HMAC token을 검증한다. 운영 환경은 `Astra:LiveOpsAuth:Authority`를 설정해 OIDC discovery metadata의 signing key와 설정된 audience, lifetime, issuer, name/role claim을 검증한다. External authority mode에서는 로컬 개발 token을 발급하지 않는다.

Audit actor는 hard-coded claim name 대신 검증된 `ClaimsIdentity`의 name/role mapping에서 추출한다. Custom OIDC `NameClaimType`, `RoleClaimType`도 authorization과 audit에서 같은 의미를 유지한다.

개발용 token endpoint는 다음 두 조건을 모두 만족할 때만 mapping한다.

- API environment가 `Development`
- `Astra:LiveOpsAuth:DevTokenEnabled=true`

Endpoint는 commit된 설정 밖에서 전달하는 32 byte 이상의 bootstrap key를 요구한다. Direct loopback request만 허용하고 forwarding header가 있으면 거부한다. 설정된 operator ID만 사용할 수 있으며 caller가 role을 선택하거나 상승시킬 수 없다. JWT signing key와 bootstrap key는 `Astra__LiveOpsAuth__SigningKey`, `Astra__LiveOpsAuth__DevTokenKey`로 주입하며 저장소에 기본값을 두지 않는다.

`Astra.Admin`은 cookie 인증 BFF 경계를 가진다. 개발용 sign-in은 loopback과 antiforgery를 검사하고 설정된 operator ID와 bootstrap key로 API token을 교환한다. 운영 환경은 PKCE를 적용한 confidential OIDC authorization code flow를 사용한다. API access token은 authentication property에서 제거해 pod-local session store에만 보관하고 browser에는 보호된 HttpOnly cookie를 전달한다. Identity token은 표준 OIDC sign-out에 필요한 경우에만 보호된 cookie에 남긴다.

OIDC callback과 logout URI는 신뢰된 public HTTPS origin으로 생성한다. Ingress TLS termination 이후 내부 HTTP 주소나 변조된 host가 authorization request에 들어가는 것을 방지한다.

각 Admin API client method는 signed-in user의 최소 role을 검사하고 같은 사용자의 access token을 전달한다. UI가 shared Supervisor 권한으로 동작하지 않는다. OIDC는 identity token에 `sub`와 설정된 Admin role claim, API access token에 동일한 LiveOps role을 발급해야 한다. 지원 role은 `LiveOpsViewer`, `LiveOpsOperator`, `LiveOpsSupervisor`다. Identity, role, access token 또는 expiry가 없으면 sign-in을 거부한다. Access token 만료 시 Admin cookie도 만료하며 refresh token은 보관하지 않는다.

현재 BFF session store는 pod-local이다. Helm은 OIDC Admin replica가 1개를 초과하면 배포를 거부한다. Pod restart 이후 다시 sign-in해야 한다. 수평 확장 전에는 shared Data Protection과 분산 encrypted token store가 필요하다.

## 역할별 권한

| Route | 최소 역할 |
|---|---|
| `GET /api/admin/content/active` | Viewer |
| `GET /api/admin/mail/...` | Viewer |
| `GET /api/admin/audit` | Viewer |
| `POST /api/admin/content/publish` | Operator |
| `POST /api/admin/mail/incident` | Operator |
| `POST /api/admin/content/rollback/{version}` | Supervisor |
| `GET /api/players/{id}/wallet` | Viewer |
| HTTP player state mutation | Operator |

Supervisor는 Operator와 Viewer policy를 만족하고 Operator는 Viewer policy를 만족한다. HTTP player route는 인증된 운영 및 simulator surface다. 운영 game client는 signed TCP session protocol을 사용한다.

## 감사 생명주기

인증된 HTTP 상태 변경 작업은 다음 순서로 기록한다.

1. API가 Orleans 호출 전에 `started` audit row를 삽입한다.
2. Actor ID, display name, role, trace ID, action, target, reason, source address와 정제된 request summary를 기록한다.
3. Domain 성공 시 `succeeded`로 변경한다.
4. Domain 검증 거부 시 안정적인 오류 코드와 함께 `rejected`로 변경한다.
5. 예외 발생 시 CLR type과 exception message를 저장하지 않고 안정적인 API 오류 코드와 함께 `failed`로 변경한다.

Token, request hash, mail body와 전체 보상 대상자 목록은 저장하지 않는다. 운영 검토에는 target count와 reward summary만 사용한다.

API와 Silo가 별도 process이므로 audit row와 Grain mutation은 하나의 DB transaction을 공유할 수 없다. Domain commit 이후 crash가 발생하면 audit 상태가 `started`로 남아 결과가 불확실함을 나타낸다. 운영자는 target과 trace ID로 결과를 reconciliation한다.

Audit completion은 `started`에서 한 번만 transition할 수 있다. 최근 기록은 Blazor `Audit Log`와 Viewer 권한 API에서 조회한다.
