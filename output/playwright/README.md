# ASTRA Visual Evidence

- Demo run: `20260712141001-359df1`
- Admin viewport: `1440 x 1000`
- Mobile QA viewport: `390 x 844`

| File | Evidence |
|---|---|
| `01-content-ops.png` | Published immutable content version, checksum, and active snapshot |
| `02-incident-mail.png` | Incident target verification, immutable mail definition, and idempotent claim inputs |
| `03-audit-log.png` | Authenticated actor trail for content, grant, gacha, incident, and mail operations |
| `04-outbox-operations.png` | Outbox backlog, published events, retry state, and intentional dead-letter fault |
| `astra-operational-dashboard.png` | API, TCP, Grain, PostgreSQL, Outbox, and burn-rate telemetry |
| `astra-observability-dashboard.png` | Baseline OpenTelemetry and Elastic APM dashboard |

## QA

- Browser console: 0 errors, 0 warnings on Content, Incident/Mail, Audit, and Outbox pages.
- Horizontal overflow: none at desktop width on all four pages.
- Mobile Content page: no horizontal overflow or incoherent overlap at 390 px.
- Screenshots contain no access token or signing key.

`outbox_payload_invalid` is an intentional fault-injection result used to demonstrate dead-letter diagnosis, not an unhandled demo failure.
