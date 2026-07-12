CREATE TABLE IF NOT EXISTS players (
    player_id uuid PRIMARY KEY,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS wallet_balances (
    player_id uuid NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    currency smallint NOT NULL,
    amount bigint NOT NULL CHECK (amount >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (player_id, currency)
);

CREATE TABLE IF NOT EXISTS inventory_items (
    player_id uuid NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    item_id text NOT NULL,
    quantity bigint NOT NULL CHECK (quantity >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (player_id, item_id)
);

CREATE TABLE IF NOT EXISTS owned_characters (
    player_id uuid NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    character_id text NOT NULL,
    rarity integer NOT NULL CHECK (rarity > 0),
    duplicate_count integer NOT NULL DEFAULT 0 CHECK (duplicate_count >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (player_id, character_id)
);

CREATE TABLE IF NOT EXISTS pity_states (
    player_id uuid NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    banner_id text NOT NULL,
    pity integer NOT NULL CHECK (pity >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (player_id, banner_id)
);

CREATE TABLE IF NOT EXISTS gacha_draw_history (
    draw_id uuid PRIMARY KEY,
    player_id uuid NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    banner_id text NOT NULL,
    content_version text NOT NULL,
    content_checksum text NOT NULL,
    draw_count integer NOT NULL CHECK (draw_count > 0),
    cost_currency smallint NOT NULL,
    cost_amount bigint NOT NULL CHECK (cost_amount > 0),
    rewards_json text NOT NULL,
    pity_before integer NOT NULL CHECK (pity_before >= 0),
    pity_after integer NOT NULL CHECK (pity_after >= 0),
    idempotency_key text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE gacha_draw_history
    DROP CONSTRAINT IF EXISTS gacha_draw_history_player_id_idempotency_key_key;

CREATE INDEX IF NOT EXISTS ix_gacha_history_player_idempotency
    ON gacha_draw_history(player_id, idempotency_key, created_at);

CREATE INDEX IF NOT EXISTS ix_gacha_history_content_version
    ON gacha_draw_history(content_version, created_at, player_id);

CREATE INDEX IF NOT EXISTS ix_gacha_history_banner
    ON gacha_draw_history(banner_id, created_at, player_id);

CREATE TABLE IF NOT EXISTS ledger_entries (
    player_id uuid NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    version bigint NOT NULL,
    currency smallint NOT NULL,
    delta bigint NOT NULL,
    balance_after bigint NOT NULL CHECK (balance_after >= 0),
    reason text NOT NULL,
    idempotency_key text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (player_id, version)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_ledger_idempotency
    ON ledger_entries(player_id, idempotency_key, version);

CREATE TABLE IF NOT EXISTS idempotency_requests (
    player_id uuid NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    idempotency_key text NOT NULL,
    request_hash text NOT NULL,
    response_body text NOT NULL,
    snapshot_body text NULL,
    completed_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (player_id, idempotency_key)
);

ALTER TABLE idempotency_requests
    ADD COLUMN IF NOT EXISTS snapshot_body text NULL;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = current_schema()
          AND table_name = 'idempotency_requests'
          AND column_name = 'response_body'
          AND data_type <> 'text') THEN
        ALTER TABLE idempotency_requests
            ALTER COLUMN response_body TYPE text
            USING response_body::text;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_idempotency_expires_at
    ON idempotency_requests(expires_at);

CREATE TABLE IF NOT EXISTS mail_definitions (
    mail_id text PRIMARY KEY,
    incident_id text NOT NULL,
    title text NOT NULL,
    body text NOT NULL,
    rewards_json text NOT NULL,
    reason text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS mail_targets (
    mail_id text NOT NULL REFERENCES mail_definitions(mail_id) ON DELETE CASCADE,
    player_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (mail_id, player_id)
);

CREATE INDEX IF NOT EXISTS ix_mail_targets_player_id
    ON mail_targets(player_id);

CREATE TABLE IF NOT EXISTS mail_claims (
    player_id uuid NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
    mail_id text NOT NULL REFERENCES mail_definitions(mail_id) ON DELETE RESTRICT,
    idempotency_key text NOT NULL,
    claimed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (player_id, mail_id)
);

CREATE TABLE IF NOT EXISTS outbox_events (
    event_id uuid PRIMARY KEY,
    event_type text NOT NULL,
    aggregate_type text NOT NULL,
    aggregate_id uuid NOT NULL,
    idempotency_key text NOT NULL,
    payload text NOT NULL,
    status text NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'processing', 'published', 'failed')),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    max_attempts integer NOT NULL DEFAULT 5 CHECK (max_attempts > 0),
    available_at timestamptz NOT NULL DEFAULT now(),
    leased_by text NULL,
    lease_until timestamptz NULL,
    last_error text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz NULL
);

CREATE INDEX IF NOT EXISTS ix_outbox_pending_available
    ON outbox_events(available_at, created_at, event_id)
    WHERE status = 'pending';

CREATE INDEX IF NOT EXISTS ix_outbox_processing_lease
    ON outbox_events(lease_until)
    WHERE status = 'processing';

CREATE INDEX IF NOT EXISTS ix_outbox_aggregate
    ON outbox_events(aggregate_type, aggregate_id, created_at);
