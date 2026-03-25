CREATE TABLE IF NOT EXISTS events (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp_utc     TEXT    NOT NULL,
    machine_name      TEXT    NOT NULL,
    user_name         TEXT    NOT NULL,
    session_id        INTEGER NOT NULL,
    event_type        INTEGER NOT NULL,
    process_name      TEXT,
    app_display_name  TEXT,
    window_title      TEXT,
    url               TEXT,
    mouse_clicks      INTEGER DEFAULT 0,
    key_presses       INTEGER DEFAULT 0,
    scroll_events     INTEGER DEFAULT 0,
    session_action    TEXT,
    is_idle           INTEGER,
    idle_duration_sec REAL,
    exported          INTEGER DEFAULT 0,
    export_batch_id   TEXT
);

CREATE INDEX IF NOT EXISTS idx_events_timestamp ON events(timestamp_utc);
CREATE INDEX IF NOT EXISTS idx_events_exported ON events(exported) WHERE exported = 0;
CREATE INDEX IF NOT EXISTS idx_events_type ON events(event_type);

CREATE TABLE IF NOT EXISTS agent_meta (
    key   TEXT PRIMARY KEY,
    value TEXT
);
