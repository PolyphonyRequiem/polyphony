using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Journal;
using Polyphony.Manifest;
using Polyphony.Models;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W15 (AB#3285): <c>polyphony lineage status --root</c>.
/// </summary>
public sealed class LineageStatusTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly string _manifestPath;
    private readonly JournalStore _store;

    public LineageStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-lineage-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
        _manifestPath = Path.Combine(_tempDir, ".polyphony", "run.yaml");
    }

    [Fact]
    public async Task Status_NoManifestAndEmptyJournal_NoJournalHistory()
    {
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("run-fresh"), _store);

        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Status(root: 5001, manifestPath: _manifestPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Root.ShouldBe(5001);
        result.CurrentRunId.ShouldBe("run-fresh");
        result.CurrentLineageHasRows.ShouldBeFalse();
        result.LooksPartitioned.ShouldBeFalse();
        result.Lineages.Count.ShouldBe(0);
        result.ManifestRunId.ShouldBeNull();
        result.ManifestError.ShouldBeNull();
        result.Verdict.ShouldStartWith("no_journal_history");
    }

    [Fact]
    public async Task Status_CurrentLineageHasRows_VerdictIsActive()
    {
        await AppendRowAsync(runId: "run-active", root: 5010, action: "branch_ensure_feature");

        WriteManifest(rootId: 5010, runId: "run-active");
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("run-active"), _store);

        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Status(root: 5010, manifestPath: _manifestPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.CurrentLineageHasRows.ShouldBeTrue();
        result.LooksPartitioned.ShouldBeFalse();
        result.Lineages.Count.ShouldBe(1);
        result.Lineages[0].RunId.ShouldBe("run-active");
        result.Lineages[0].IsCurrent.ShouldBeTrue();
        result.Lineages[0].RowCount.ShouldBe(1);
        result.ManifestRunId.ShouldBe("run-active");
        result.Verdict.ShouldStartWith("current_lineage_active");
    }

    [Fact]
    public async Task Status_PriorLineageOnlyAndFreshCurrent_LooksPartitioned()
    {
        await AppendRowAsync(runId: "run-old", root: 5020, action: "branch_ensure_feature");

        WriteManifest(rootId: 5020, runId: "run-old");
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("run-new"), _store);

        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Status(root: 5020, manifestPath: _manifestPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.CurrentLineageHasRows.ShouldBeFalse();
        result.LooksPartitioned.ShouldBeTrue();
        result.Lineages.Count.ShouldBe(1);
        result.Lineages[0].RunId.ShouldBe("run-old");
        result.Lineages[0].IsCurrent.ShouldBeFalse();
        result.Verdict.ShouldStartWith("journal_partitioned");
        result.Verdict.ShouldContain("run-new");
    }

    [Fact]
    public async Task Status_ManualLineage_VerdictIsManual()
    {
        // RunContext.HasManualLineage falls back to the `manual_` prefix
        // check, so an explicit run id starting with `manual_` is the
        // simplest way to simulate "launcher didn't export POLYPHONY_RUN_ID".
        var command = new LineageCommands(
            JournalTestSupport.CreateRunContext("manual_no_env"),
            _store);

        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Status(root: 5030, manifestPath: _manifestPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.CurrentLineageIsManual.ShouldBeTrue();
        result.Verdict.ShouldStartWith("current_lineage_manual");
    }

    [Fact]
    public async Task Status_ManifestRunIdMismatch_VerdictCalledOut()
    {
        await AppendRowAsync(runId: "run-current", root: 5040, action: "branch_ensure_feature");
        WriteManifest(rootId: 5040, runId: "run-other");

        var command = new LineageCommands(JournalTestSupport.CreateRunContext("run-current"), _store);

        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Status(root: 5040, manifestPath: _manifestPath));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.ManifestRunId.ShouldBe("run-other");
        result.CurrentRunId.ShouldBe("run-current");
        result.Verdict.ShouldStartWith("manifest_lineage_mismatch");
    }

    [Fact]
    public async Task Status_RequiresRoot_HaltsWithoutFlag()
    {
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("run-x"), _store);
        var (exitCode, _) = await CaptureConsoleAsync(() => command.Status(manifestPath: _manifestPath));
        exitCode.ShouldNotBe(ExitCodes.Success);
    }

    private async Task AppendRowAsync(string runId, int root, string action)
    {
        var id = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = runId,
                RootId = root,
                WorkItemId = root,
                Action = action,
                Target = $"feature/{root}",
                StartedAt = 1_700_000_000_000,
            },
            CancellationToken.None);
        await _store.RecordEndAsync(id, JournalOutcome.Success, null, null, null, null, CancellationToken.None);
    }

    private void WriteManifest(int rootId, string runId)
    {
        var manifest = new RunManifest
        {
            RootId = rootId,
            RunId = runId,
            PlatformProject = "dev.azure.com/test/Test",
            CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = "test",
            BranchModelVersion = 1,
        };
        RunManifestStore.Save(_manifestPath, manifest);
    }

    private static LineageStatusResult Deserialize(string output)
    {
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.LineageStatusResult);
        result.ShouldNotBeNull();
        return result;
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
            // Best-effort cleanup; SQLite can hold a transient handle on Windows.
        }
    }
}
