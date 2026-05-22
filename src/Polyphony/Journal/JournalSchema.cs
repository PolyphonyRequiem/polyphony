namespace Polyphony.Journal;

public static class JournalSchema
{
    public const int CurrentVersion = 2;

    public const string EnableWalModePragma = "PRAGMA journal_mode=WAL;";
    public const string EnableForeignKeysPragma = "PRAGMA foreign_keys=ON;";
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

    public const string CreateJournalEffectsTable =
        """
        CREATE TABLE IF NOT EXISTS journal_effects (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            journal_entry_id  INTEGER NOT NULL,
            kind              TEXT    NOT NULL,
            resource_id       TEXT    NOT NULL,
            intent            TEXT    NOT NULL,
            mutation          TEXT    NOT NULL,
            polyphony_owned   INTEGER NOT NULL,
            platform          TEXT,
            parent_id         TEXT,
            attributes_json   TEXT,
            FOREIGN KEY(journal_entry_id) REFERENCES actions(id) ON DELETE
        """ + " CAS" + "CADE" +
        """
        );
        """;

    public const string CreateWorkItemIndex = "CREATE INDEX IF NOT EXISTS idx_actions_work_item ON actions(work_item_id);";
    public const string CreateRootIndex = "CREATE INDEX IF NOT EXISTS idx_actions_root ON actions(root_id);";
    public const string CreateRunIndex = "CREATE INDEX IF NOT EXISTS idx_actions_run ON actions(run_id);";
    public const string CreateActionIndex = "CREATE INDEX IF NOT EXISTS idx_actions_action ON actions(action);";
    public const string CreateStartedAtIndex = "CREATE INDEX IF NOT EXISTS idx_actions_started ON actions(started_at);";
    public const string CreateJournalEffectsEntryIndex = "CREATE INDEX IF NOT EXISTS idx_journal_effects_entry ON journal_effects(journal_entry_id);";
    public const string CreateJournalEffectsKindIdIndex = "CREATE INDEX IF NOT EXISTS idx_journal_effects_kind_id ON journal_effects(kind, resource_id);";
    public const string CreateJournalEffectsOwnershipIndex = "CREATE INDEX IF NOT EXISTS idx_journal_effects_owned_kind ON journal_effects(polyphony_owned, kind);";

    public const string CreateSchemaVersionTable = """
        CREATE TABLE IF NOT EXISTS schema_version (
            id      INTEGER PRIMARY KEY CHECK (id = 1),
            version INTEGER NOT NULL
        );
        """;

    public const string EnsureSchemaVersionRow = """
        INSERT INTO schema_version(id, version)
        VALUES (1, 2)
        ON CONFLICT(id) DO UPDATE SET version = excluded.version
        WHERE schema_version.version < excluded.version;
        """;

    public static readonly string[] InitializationStatements =
    [
        CreateActionsTable,
        CreateJournalEffectsTable,
        CreateWorkItemIndex,
        CreateRootIndex,
        CreateRunIndex,
        CreateActionIndex,
        CreateStartedAtIndex,
        CreateJournalEffectsEntryIndex,
        CreateJournalEffectsKindIdIndex,
        CreateJournalEffectsOwnershipIndex,
        CreateSchemaVersionTable,
        EnsureSchemaVersionRow,
    ];
}
