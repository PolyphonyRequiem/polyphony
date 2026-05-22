using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal;

public sealed class JournalLocatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JournalLocator _locator = new();

    public JournalLocatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-journal-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ResolveJournalPath_FindsJournalAtCurrentDirectory()
    {
        var cwd = Path.Combine(_tempDir, "cwd");
        Directory.CreateDirectory(Path.Combine(cwd, ".polyphony-state"));

        var path = _locator.ResolveJournalPath(cwd);

        path.ShouldBe(Path.Combine(cwd, ".polyphony-state", "journal.db"));
    }

    [Fact]
    public void ResolveJournalPath_FindsJournalAtParentDirectory()
    {
        var parent = Path.Combine(_tempDir, "parent");
        var child = Path.Combine(parent, "child", "grandchild");
        Directory.CreateDirectory(Path.Combine(parent, ".polyphony-state"));
        Directory.CreateDirectory(child);

        var path = _locator.ResolveJournalPath(child);

        path.ShouldBe(Path.Combine(parent, ".polyphony-state", "journal.db"));
    }

    [Fact]
    public void ResolveJournalPath_WhenJournalNotFound_DefaultsToStartDirectory()
    {
        var cwd = Path.Combine(_tempDir, "default-root");
        Directory.CreateDirectory(cwd);

        var path = _locator.ResolveJournalPath(cwd);

        path.ShouldBe(Path.Combine(cwd, ".polyphony-state", "journal.db"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
