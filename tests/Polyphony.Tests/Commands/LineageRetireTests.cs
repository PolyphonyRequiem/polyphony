using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Journal;
using Polyphony.Models;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W12 (AB#3293): <c>polyphony lineage retire</c> + the
/// reset-pipeline integration that calls it with
/// <c>--all-active --reason "reset root"</c>.
/// </summary>
public sealed class LineageRetireTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly JournalStore _store;

    public LineageRetireTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-lineage-retire-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
    }

    [Fact]
    public async Task Retire_RunIdAndAllActiveBoth_ConfigError()
    {
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Retire(root: 1, runId: "x", allActive: true, execute: true));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = Deserialize(output);
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("mutually exclusive");
    }

    [Fact]
    public async Task Retire_NeitherRunIdNorAllActive_ConfigError()
    {
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Retire(root: 1, execute: true));

        exitCode.ShouldBe(ExitCodes.ConfigError);
        var result = Deserialize(output);
        result.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task Retire_AllActive_DryRun_ReportsButDoesNotMutate()
    {
        await RecordLineageAsync("01JCWWWWWWWWWWWWWWWWWWWWWW", 7777);
        await RecordLineageAsync("01JCXXXXXXXXXXXXXXXXXXXXXX", 7777);

        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Retire(root: 7777, allActive: true, execute: false));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Success.ShouldBeTrue();
        result.Executed.ShouldBeFalse();
        result.RetiredRunIds.ShouldBe(
            new[] { "01JCWWWWWWWWWWWWWWWWWWWWWW", "01JCXXXXXXXXXXXXXXXXXXXXXX" },
            ignoreOrder: true);

        // No mutation: GetLineagesAsync still returns both as active.
        var post = await _store.GetLineagesAsync(7777, CancellationToken.None);
        post.Count.ShouldBe(2);
        post.All(l => l.RetiredAt is null).ShouldBeTrue();
    }

    [Fact]
    public async Task Retire_AllActive_Execute_TombstonesEveryActiveLineage()
    {
        await RecordLineageAsync("01JCAAAAAAAAAAAAAAAAAAAAAA", 8888);
        await RecordLineageAsync("01JCBBBBBBBBBBBBBBBBBBBBBB", 8888);

        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Retire(root: 8888, allActive: true, reason: "cleanup", execute: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Success.ShouldBeTrue();
        result.Executed.ShouldBeTrue();
        result.RetiredRunIds.Count.ShouldBe(2);

        var post = await _store.GetLineagesAsync(8888, CancellationToken.None);
        post.All(l => l.RetiredAt is not null).ShouldBeTrue();
        post.All(l => l.RetiredReason == "cleanup").ShouldBeTrue();
    }

    [Fact]
    public async Task Retire_AllActive_SkipsAlreadyRetiredLineages()
    {
        await RecordLineageAsync("01JCYYYYYYYYYYYYYYYYYYYYYY", 9999);
        await RecordLineageAsync("01JCZZZZZZZZZZZZZZZZZZZZZZ", 9999);
        // Tombstone the first one ahead of time.
        await _store.RetireLineageAsync("01JCYYYYYYYYYYYYYYYYYYYYYY", 9999, "earlier", CancellationToken.None);

        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Retire(root: 9999, allActive: true, execute: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Success.ShouldBeTrue();
        result.RetiredRunIds.ShouldBe(new[] { "01JCZZZZZZZZZZZZZZZZZZZZZZ" });
        result.SkippedRunIds.ShouldContain("01JCYYYYYYYYYYYYYYYYYYYYYY");

        // Original tombstone preserved (reason unchanged).
        var earlier = (await _store.GetLineagesAsync(9999, CancellationToken.None))
            .Single(l => l.RunId == "01JCYYYYYYYYYYYYYYYYYYYYYY");
        earlier.RetiredReason.ShouldBe("earlier");
    }

    [Fact]
    public async Task Retire_SpecificRunId_TombstonesJustThatLineage()
    {
        await RecordLineageAsync("01JCKEEPKEEPKEEPKEEPKEEPKE", 12000);
        await RecordLineageAsync("01JCDROPDROPDROPDROPDROPDR", 12000);

        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Retire(root: 12000, runId: "01JCDROPDROPDROPDROPDROPDR", execute: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Success.ShouldBeTrue();
        result.RetiredRunIds.ShouldBe(new[] { "01JCDROPDROPDROPDROPDROPDR" });

        var lineages = await _store.GetLineagesAsync(12000, CancellationToken.None);
        var keep = lineages.Single(l => l.RunId == "01JCKEEPKEEPKEEPKEEPKEEPKE");
        keep.RetiredAt.ShouldBeNull();
        var drop = lineages.Single(l => l.RunId == "01JCDROPDROPDROPDROPDROPDR");
        drop.RetiredAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Retire_SpecificRunId_MissingLineage_SuccessWithEmptyRetired()
    {
        // No lineages recorded for this root.
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(
            () => command.Retire(root: 13000, runId: "01JCMISSINGMISSINGMISSINGMI", execute: true));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Success.ShouldBeTrue();
        result.RetiredRunIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Status_AfterRetire_SurfacesRetiredAtAndReason()
    {
        // Use the auto-record path so we have an action row backing the lineage.
        await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = "01JCRETIRETIRETIRETIRETIRT",
                RootId = 14000,
                WorkItemId = 14000,
                Action = "plan_seed_children",
                Target = "wi:14000",
                StartedAt = 1_800_000_000_000,
            },
            CancellationToken.None);
        await _store.RetireLineageAsync("01JCRETIRETIRETIRETIRETIRT", 14000, "operator wipe", CancellationToken.None);

        var statusCommand = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (_, statusOutput) = await CaptureConsoleAsync(
            () => statusCommand.Status(root: 14000, manifestPath: ""));
        var status = JsonSerializer.Deserialize(statusOutput, PolyphonyJsonContext.Default.LineageStatusResult);
        status.ShouldNotBeNull();
        var observation = status.Lineages.Single(l => l.RunId == "01JCRETIRETIRETIRETIRETIRT");
        observation.RetiredAt.ShouldNotBeNull();
        observation.RetiredReason.ShouldBe("operator wipe");
    }

    private Task RecordLineageAsync(string runId, int root)
        => _store.RecordLineageAsync(runId, root, host: "test-host", user: "test-user", CancellationToken.None);

    private static LineageRetireResult Deserialize(string output)
    {
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.LineageRetireResult);
        result.ShouldNotBeNull();
        return result;
    }

    public override void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }
}
