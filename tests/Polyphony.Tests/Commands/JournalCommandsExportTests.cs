using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class JournalCommandsExportTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly JournalStore _store;
    private readonly JournalCommands _command;

    public JournalCommandsExportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-journal-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
        _command = new JournalCommands(_store);
    }

    [Fact]
    public async Task Export_CopiesDatabaseAndReportsBytes()
    {
        var actionId = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = "run-export",
                RootId = 3260,
                WorkItemId = 3260,
                Action = "branch_ensure_feature",
                Target = "feature/3260",
                StartedAt = 1_700_000_000_000,
            },
            CancellationToken.None);
        await _store.RecordEndAsync(actionId, JournalOutcome.Success, null, null, null, null, CancellationToken.None);

        var destination = Path.Combine(_tempDir, "exports", "journal-copy.db");
        var (exitCode, output) = await CaptureConsoleAsync(() => _command.Export(destination));
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalExportResult);

        exitCode.ShouldBe(ExitCodes.Success);
        File.Exists(destination).ShouldBeTrue();
        result.ShouldNotBeNull();
        result.SourcePath.ShouldBe(_store.DatabasePath);
        result.DestinationPath.ShouldBe(Path.GetFullPath(destination));
        result.BytesCopied.ShouldBe(new FileInfo(destination).Length);
    }

    public override void Dispose()
    {
        base.Dispose();
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
