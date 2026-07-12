CREATE TABLE IF NOT EXISTS admin_operation_audit (
    audit_id uuid PRIMARY KEY,
    correlation_id text NOT NULL,
    actor_id text NOT NULL,
    actor_display_name text NOT NULL,
    actor_role text NOT NULL,
    action text NOT NULL,
    target_type text NOT NULL,
    target_id text NOT NULL,
    reason text NOT NULL,
    request_summary jsonb NOT NULL,
    status text NOT NULL CHECK (status IN ('started', 'succeeded', 'rejected', 'failed')),
    result_summary jsonb NULL,
    error_code text NULL,
    source_ip text NULL,
    started_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    CHECK (
        (status = 'started' AND completed_at IS NULL) OR
        (status <> 'started' AND completed_at IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS ix_admin_audit_started_at
    ON admin_operation_audit(started_at DESC, audit_id DESC);

CREATE INDEX IF NOT EXISTS ix_admin_audit_actor
    ON admin_operation_audit(actor_id, started_at DESC);

CREATE INDEX IF NOT EXISTS ix_admin_audit_action_status
    ON admin_operation_audit(action, status, started_at DESC);
