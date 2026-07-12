# Content Lifecycle

## Ownership

- PostgreSQL is the source of truth in multi-Silo environments.
- `content_snapshots` stores immutable, checksummed versions.
- `active_content` stores the single active version and a monotonic generation.
- Each Silo owns one in-process `ActiveContentCache`; game commands read only this cache.
- `InMemoryContentSnapshotStore` is limited to single-Silo development and tests.

## Publish

1. `EventConfigGrain` validates and canonicalizes the command.
2. One PostgreSQL transaction inserts the immutable snapshot and advances `active_content`.
3. The same transaction calls `pg_notify`. PostgreSQL delivers it only after commit.
4. The publishing Silo updates its local cache before returning.
5. Every other Silo receives the notification and reloads the committed active snapshot.

Retrying the currently active version with the same checksum is a no-op: it does not advance the generation or emit another notification. Publishing an existing inactive version is rejected as `content.version.inactive`; the explicit rollback operation must be used to reactivate history. Reusing a version with a different checksum is rejected as `content.version.conflict`.

## Rollback

Rollback never edits or deletes historical content. It atomically moves `active_content` to an existing immutable version, increments the generation, and emits the same cache notification.

## Failure Behavior

- SQL files are serialized with a PostgreSQL advisory lock and recorded in `astra_schema_migrations` with checksums. Applied files are immutable; schema changes require a new numbered migration.
- A transaction rollback emits no notification and changes no active pointer.
- A missed notification is repaired by periodic reconciliation; the default interval is 30 seconds.
- A disconnected listener retains its last valid snapshot and reconnects with bounded backoff.
- Silo startup loads the active snapshot before Orleans joins the cluster or opens its gateway in PostgreSQL mode.
- A fresh Silo cannot serve content-dependent commands while PostgreSQL is unavailable.

LISTEN/NOTIFY provides fast cache convergence, not a linearizable all-Silo cutover barrier. Scheduled content should be published before its gameplay start time. An emergency rollback has a short propagation window bounded by notification delivery or the reconciliation interval; a strict zero-window cutover would require an acknowledged Silo barrier and is intentionally outside this portfolio scope.

Production deployments should run schema application as a single pre-deployment job and keep `Astra:ApplyDatabaseSchema=false` on ordinary Silo replicas. The embedded initializer remains enabled for local development and CI verification.
