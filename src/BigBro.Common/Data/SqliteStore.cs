using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using BigBro.Common.Models;

namespace BigBro.Common.Data;

public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteStore> _logger;

    public SqliteStore(string dbPath, ILogger<SqliteStore> logger)
    {
        _logger = logger;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        _connection = new SqliteConnection(connStr);
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA temp_store=MEMORY;");

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        // Use typeof to get the correct assembly in single-file publish scenarios
        // where Assembly.GetExecutingAssembly() may return the host assembly.
        var assembly = typeof(SqliteStore).Assembly;
        using var stream = assembly.GetManifestResourceStream("BigBro.Common.Data.Schema.sql");

        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            Execute(sql);
            _logger.LogInformation("Database schema initialized from embedded resource.");
            return;
        }

        // Fallback: apply schema inline if the embedded resource can't be resolved.
        _logger.LogWarning("Embedded Schema.sql not found. Applying schema inline.");
        Execute(FallbackSchema);
        _logger.LogInformation("Database schema initialized from inline fallback.");
    }

    private const string FallbackSchema = """
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
        """;

    public void InsertEvents(IReadOnlyList<ActivityEvent> events)
    {
        if (events.Count == 0) return;

        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();

        cmd.CommandText = @"
            INSERT INTO events (
                timestamp_utc, machine_name, user_name, session_id, event_type,
                process_name, app_display_name, window_title, url,
                mouse_clicks, key_presses, scroll_events,
                session_action, is_idle, idle_duration_sec
            ) VALUES (
                $ts, $machine, $user, $sid, $type,
                $proc, $app, $title, $url,
                $clicks, $keys, $scrolls,
                $session_action, $idle, $idle_dur
            )";

        var pTs = cmd.Parameters.Add("$ts", SqliteType.Text);
        var pMachine = cmd.Parameters.Add("$machine", SqliteType.Text);
        var pUser = cmd.Parameters.Add("$user", SqliteType.Text);
        var pSid = cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pType = cmd.Parameters.Add("$type", SqliteType.Integer);
        var pProc = cmd.Parameters.Add("$proc", SqliteType.Text);
        var pApp = cmd.Parameters.Add("$app", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pUrl = cmd.Parameters.Add("$url", SqliteType.Text);
        var pClicks = cmd.Parameters.Add("$clicks", SqliteType.Integer);
        var pKeys = cmd.Parameters.Add("$keys", SqliteType.Integer);
        var pScrolls = cmd.Parameters.Add("$scrolls", SqliteType.Integer);
        var pSessionAction = cmd.Parameters.Add("$session_action", SqliteType.Text);
        var pIdle = cmd.Parameters.Add("$idle", SqliteType.Integer);
        var pIdleDur = cmd.Parameters.Add("$idle_dur", SqliteType.Real);

        cmd.Prepare();

        foreach (var e in events)
        {
            pTs.Value = e.TimestampUtc.ToString("o");
            pMachine.Value = e.MachineName;
            pUser.Value = e.UserName;
            pSid.Value = e.SessionId;
            pType.Value = (int)e.EventType;
            pProc.Value = (object?)e.ProcessName ?? DBNull.Value;
            pApp.Value = (object?)e.AppDisplayName ?? DBNull.Value;
            pTitle.Value = (object?)e.WindowTitle ?? DBNull.Value;
            pUrl.Value = (object?)e.Url ?? DBNull.Value;
            pClicks.Value = e.MouseClicks;
            pKeys.Value = e.KeyPresses;
            pScrolls.Value = e.ScrollEvents;
            pSessionAction.Value = e.SessionAction.HasValue ? e.SessionAction.Value.ToString() : DBNull.Value;
            pIdle.Value = e.IsIdle.HasValue ? (e.IsIdle.Value ? 1 : 0) : DBNull.Value;
            pIdleDur.Value = (object?)e.IdleDurationSec ?? DBNull.Value;

            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        _logger.LogDebug("Flushed {Count} events to SQLite.", events.Count);
    }

    public List<ActivityEvent> GetUnexportedEvents(int limit = 10000)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM events WHERE exported = 0 ORDER BY id LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<ActivityEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadEvent(reader));
        }
        return results;
    }

    public List<long> GetUnexportedEventIds(int limit = 10000)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM events WHERE exported = 0 ORDER BY id LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    public void MarkExported(IReadOnlyList<long> ids, string batchId)
    {
        if (ids.Count == 0) return;

        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE events SET exported = 1, export_batch_id = $batch WHERE id = $id";
        var pBatch = cmd.Parameters.Add("$batch", SqliteType.Text);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        pBatch.Value = batchId;
        cmd.Prepare();

        foreach (var id in ids)
        {
            pId.Value = id;
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public long GetDatabaseSizeBytes()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size();";
        return (long)(cmd.ExecuteScalar() ?? 0);
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO agent_meta (key, value) VALUES ($key, $val)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$val", value);
        cmd.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM agent_meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static ActivityEvent ReadEvent(SqliteDataReader reader)
    {
        var sessionActionStr = reader.IsDBNull(reader.GetOrdinal("session_action"))
            ? null : reader.GetString(reader.GetOrdinal("session_action"));
        SessionAction? sessionAction = sessionActionStr is not null
            ? Enum.Parse<SessionAction>(sessionActionStr) : null;

        var isIdleOrd = reader.GetOrdinal("is_idle");
        bool? isIdle = reader.IsDBNull(isIdleOrd) ? null : reader.GetInt32(isIdleOrd) == 1;

        var idleDurOrd = reader.GetOrdinal("idle_duration_sec");
        double? idleDur = reader.IsDBNull(idleDurOrd) ? null : reader.GetDouble(idleDurOrd);

        return new ActivityEvent
        {
            TimestampUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp_utc"))),
            MachineName = reader.GetString(reader.GetOrdinal("machine_name")),
            UserName = reader.GetString(reader.GetOrdinal("user_name")),
            SessionId = reader.GetInt32(reader.GetOrdinal("session_id")),
            EventType = (EventType)reader.GetInt32(reader.GetOrdinal("event_type")),
            ProcessName = reader.IsDBNull(reader.GetOrdinal("process_name")) ? null : reader.GetString(reader.GetOrdinal("process_name")),
            AppDisplayName = reader.IsDBNull(reader.GetOrdinal("app_display_name")) ? null : reader.GetString(reader.GetOrdinal("app_display_name")),
            WindowTitle = reader.IsDBNull(reader.GetOrdinal("window_title")) ? null : reader.GetString(reader.GetOrdinal("window_title")),
            Url = reader.IsDBNull(reader.GetOrdinal("url")) ? null : reader.GetString(reader.GetOrdinal("url")),
            MouseClicks = reader.GetInt32(reader.GetOrdinal("mouse_clicks")),
            KeyPresses = reader.GetInt32(reader.GetOrdinal("key_presses")),
            ScrollEvents = reader.GetInt32(reader.GetOrdinal("scroll_events")),
            SessionAction = sessionAction,
            IsIdle = isIdle,
            IdleDurationSec = idleDur
        };
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
