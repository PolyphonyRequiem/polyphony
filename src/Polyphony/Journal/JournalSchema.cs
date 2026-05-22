namespace Polyphony.Journal;

public static class JournalSchema
{
    public const int CurrentVersion = 1;

    public const string EnableWalModePragma = "PRAGMA journal_mode=WAL;";
    public const string WalCheckpointPragma = "PRAGMA wal_checkpoint(TRUNCATE);";

    public const string CreateActionsTable = """
        CREATE TABLE IF NOT EXISTS actions (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id          TEXT    NOT NULL,
            root_id         INTEGER,
            work_item_id    INTEGER,
            action          TEXT    NOT NULL,
            target          TEXT    NOT NULL,
            started_at      INTEGER NOT NULL,
            finished_at     INTEGER,
            outcome         TEXT,
            error_code      TEXT,
            error_message   TEXT,
            payload_json    TEXT
        );
        """;

    public const string CreateWorkItemIndex = "CREATE INDEX IF NOT EXISTS idx_actions_work_item ON actions(work_item_id);";
    public const string CreateRootIndex = "CREATE INDEX IF NOT EXISTS idx_actions_root ON actions(root_id);";
    public const string CreateRunIndex = "CREATE INDEX IF NOT EXISTS idx_actions_run ON actions(run_id);";
    public const string CreateActionIndex = "CREATE INDEX IF NOT EXISTS idx_actions_action ON actions(action);";
    public const string CreateStartedAtIndex = "CREATE INDEX IF NOT EXISTS idx_actions_started ON actions(started_at);";

    public const string CreateSchemaVersionTable = """
        CREATE TABLE IF NOT EXISTS schema_version (
            id      INTEGER PRIMARY KEY CHECK (id = 1),
            version INTEGER NOT NULL
        );
        """;

    public const string EnsureSchemaVersionRow = """
        INSERT INTO schema_version(id, version)
        VALUES (1, 1)
        ON CONFLICT(id) DO NOTHING;
        """;

    public static readonly string[] InitializationStatements =
    [
        CreateActionsTable,
        CreateWorkItemIndex,
        CreateRootIndex,
        CreateRunIndex,
        CreateActionIndex,
        CreateStartedAtIndex,
        CreateSchemaVersionTable,
        EnsureSchemaVersionRow,
    ];
}
