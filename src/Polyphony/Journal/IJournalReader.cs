namespace Polyphony.Journal;

/// <summary>
/// Read-only view of the journal database. Implementations MUST NOT
/// create, schema-initialize, or otherwise mutate the underlying SQLite
/// file. Use this contract for diagnostic surfaces
/// (<c>polyphony journal {query,owned,has,drift}</c>) and any other
/// consumer that only reads the journal — depending on
/// <see cref="IJournalStore"/> from a read-only path risks side-effecting
/// the schema, defeating the diagnostic invariant.
/// </summary>
/// <remarks>
/// The companion writer interface <see cref="IJournalStore"/> extends this
/// reader, so production code that already holds an
/// <c>IJournalStore</c> continues to work. New code that only reads
/// should depend on <see cref="IJournalReader"/> so the DI container can
/// route it to the read-only <see cref="JournalReader"/> implementation.
/// </remarks>
public interface IJournalReader
{
    string DatabasePath { get; }

    Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct);

    Task ExportAsync(string destinationPath, CancellationToken ct);
}
