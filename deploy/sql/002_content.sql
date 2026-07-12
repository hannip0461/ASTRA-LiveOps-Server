CREATE TABLE IF NOT EXISTS content_snapshots (
    version text PRIMARY KEY,
    checksum text NOT NULL,
    snapshot_json jsonb NOT NULL,
    published_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS active_content (
    singleton_id smallint PRIMARY KEY CHECK (singleton_id = 1),
    version text NOT NULL REFERENCES content_snapshots(version) ON DELETE RESTRICT,
    generation bigint NOT NULL CHECK (generation > 0),
    activated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_content_snapshots_published_at
    ON content_snapshots(published_at DESC, version);
