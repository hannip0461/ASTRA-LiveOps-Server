# API Boundary

## Error Contract

All empty HTTP failures and handled exceptions use `application/problem+json`. The response includes:

- `status`: HTTP status code;
- `code`: stable machine-readable ASTRA code;
- `traceId`: distributed trace correlation value;
- `type`: `urn:astra:problem:{code}`;
- `errors`: field messages for validation failures only.

Authentication and authorization failures use `authentication_required` and `permission_denied`. Domain failures are translated without returning exception messages or stack traces. Examples include `idempotency_conflict`, `insufficient_currency`, `content_mismatch`, and `mail_already_claimed`. Unexpected failures use `internal_error`; unavailable Orleans or PostgreSQL dependencies use `dependency_unavailable`.

## Validation Boundary

HTTP input is validated and normalized before an audit intent or Grain call. The boundary rejects null nested content, unsupported enums, empty GUIDs, unsafe identifiers, oversized collections, invalid time ranges, and numeric values outside operational limits.

Key limits:

| Input | Limit |
| --- | --- |
| Request body | 1 MiB |
| Identifier / idempotency key | 128 ASCII identifier characters |
| Operation reason | 500 characters |
| Currency amount | 1 to 1,000,000,000,000 |
| Content banners | 1 to 100 |
| Rewards per banner | 1 to 500 |
| Mail targets | 1 to 10,000 |
| Mail rewards | 1 to 20 |
| Audit page size | 1 to 200 |

The server regenerates request hashes after normalization; client-provided hashes are never trusted.

## Rate Limits

Fixed-window limits are partitioned by authenticated actor ID. A refreshed token does not create a new partition.

| Surface | Viewer | Operator | Supervisor |
| --- | ---: | ---: | ---: |
| Read requests / minute | 120 | 180 | 240 |
| Mutation requests / minute | n/a | 30 | 60 |

Development token issuance is limited to 20 requests per source address per minute. Rejections return `429 rate_limited` and a `Retry-After` header. Configuration lives under `Astra:RateLimits` and is validated at startup.
