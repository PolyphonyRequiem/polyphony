using Microsoft.Data.Sqlite;

namespace Polyphony.Journal;

/// <summary>
/// Read-only journal accessor. Opens the SQLite file with
/// <c>Mode=ReadOnly</c>, performs no schema initialization, and surfaces
/// "journal missing" as a typed exception rather than silently creating
/// the database. Use this for diagnostic surfaces that must not
/// side-effect the journal (drift analysis, owned-resource reports,
/// arbitrary user-issued queries).
/// </summary>
/// <remarks>
/// Mirrors <see cref="JournalStore"/>'s read path but never touches the
/// schema. The shared SQL lives in <see cref="JournalQueryRunner"/> so
/// the SELECT layout stays in one place.
/// </remarks>
public sealed class JournalReader : IJournalReader
{
    private readonly string _databasePath;

    public JournalReader(IJournalLocator locator)
        : this(locator.ResolveJournalPath())
    {
    }

    internal JournalReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => _databasePath;

    public async Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await JournalQueryRunner.QueryAsync(connection, query, ct).ConfigureAwait(false);
    }

    public async Task ExportAsync(string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var destination = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        if (File.Exists(destination))
            File.Delete(destination);

        await using var sourceConnection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var destinationConnection = new SqliteConnection($"Data Source={destination};Mode=ReadWriteCreate;Pooling=False");
        await destinationConnection.OpenAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        sourceConnection.BackupDatabase(destinationConnection);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        if (!File.Exists(_databasePath))
        {
            throw new JournalMissingException(_databasePath);
        }

        var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}

/// <summary>
/// Raised by <see cref="JournalReader"/> when the journal database file
/// does not exist. Distinct from a connection-error so diagnostic
/// surfaces can surface "no journal for this root" as a first-class
/// outcome instead of either creating an empty database (the previous
/// behavior of using <see cref="JournalStore"/> from a read path) or
/// returning a misleading empty result set.
/// </summary>
public sealed class JournalMissingException : Exception
{
    public JournalMissingException(string databasePath)
        : base($"Journal database not found at '{databasePath}'.")
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }
}
