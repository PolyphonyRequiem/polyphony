namespace Polyphony.Journal;

public interface IJournalLocator
{
    string ResolveJournalPath(string? startDir = null);
}

public sealed class JournalLocator : IJournalLocator
{
    private const string StateDirectoryName = ".polyphony-state";
    private const string JournalFileName = "journal.db";

    public string ResolveJournalPath(string? startDir = null)
    {
        var origin = Path.GetFullPath(startDir ?? Directory.GetCurrentDirectory());
        var current = origin;

        while (current is not null)
        {
            var stateDir = Path.Combine(current, StateDirectoryName);
            if (Directory.Exists(stateDir))
                return Path.Combine(stateDir, JournalFileName);

            current = Directory.GetParent(current)?.FullName;
        }

        return Path.Combine(origin, StateDirectoryName, JournalFileName);
    }
}
