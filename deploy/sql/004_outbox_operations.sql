ALTER TABLE outbox_events
    ADD COLUMN IF NOT EXISTS manual_replay_count integer NOT NULL DEFAULT 0
        CHECK (manual_replay_count >= 0),
    ADD COLUMN IF NOT EXISTS dead_lettered_at timestamptz NULL;

ALTER TABLE outbox_events
    DROP CONSTRAINT IF EXISTS outbox_events_status_check;

UPDATE outbox_events
SET status = 'dead_letter',
    dead_lettered_at = COALESCE(dead_lettered_at, now())
WHERE status = 'failed';

ALTER TABLE outbox_events
    ADD CONSTRAINT outbox_events_status_check
        CHECK (status IN ('pending', 'processing', 'published', 'dead_letter'));

CREATE INDEX IF NOT EXISTS ix_outbox_dead_letter
    ON outbox_events(dead_lettered_at DESC, event_id DESC)
    WHERE status = 'dead_letter';

CREATE TABLE IF NOT EXISTS operational_event_deliveries (
    consumer_name text NOT NULL,
    event_id uuid NOT NULL,
    event_type text NOT NULL,
    aggregate_id uuid NOT NULL,
    summary jsonb NOT NULL,
    consumed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (consumer_name, event_id)
);

CREATE INDEX IF NOT EXISTS ix_operational_deliveries_consumed
    ON operational_event_deliveries(consumed_at DESC, event_id DESC);
