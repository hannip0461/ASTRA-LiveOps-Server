# Working Rules

## Scope

Implementation must stay focused on two demo scenarios:

1. Gacha/pity/wallet/inventory consistency
2. Incident compensation through target snapshot and targeted mail

Do not expand into full attendance, pass, shop, mission, combat, or Unity client work unless explicitly needed for the demo.

## Idempotency

Request idempotency does not use PENDING rows.

Flow:

1. API or TCP Gateway passes Idempotency-Key to PlayerAccountGrain.
2. Grain checks completed idempotency response.
3. If missing, domain changes and completed response_body are committed in one DB transaction.
4. Retry after commit returns the stored response_body.

Outbox and compensation commands may still use PENDING-like processing states.

## TCP Gateway

TCP Gateway must not call HTTP API.

Accepted:

```text
HTTP API    -> Orleans Client -> Grain
TCP Gateway -> Orleans Client -> Grain
```

Rejected:

```text
TCP Gateway -> HTTP API -> Orleans
```

## Docker Disk Safety

- Do not start heavy services unless needed.
- PostgreSQL and Redis are enough for early development.
- ELK must stay behind a compose profile.
- Do not run broad prune commands without explicit user approval.
- Prefer targeted cleanup of containers, volumes, and build cache created by this project.
