CREATE INDEX IF NOT EXISTS ix_outbox_published_retention
    ON outbox_events(published_at, event_id)
    WHERE status = 'published';
