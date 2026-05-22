using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class JournalCommandsShowTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly JournalStore _store;
    private readonly JournalCommands _command;

    public JournalCommandsShowTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-journal-show-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
        _command = new JournalCommands(_store);
    }

    [Fact]
    public async Task Show_EmptyJournal_ReturnsSuccessWithEmptyResult()
    {
        var (exitCode, output) = await CaptureConsoleAsync(() => _command.Show());
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalShowResult);

        exitCode.ShouldBe(ExitCodes.Success);
        result.ShouldNotBeNull();
        result.Count.ShouldBe(0);
        result.Entries.ShouldBeEmpty();
        result.Filters.ShouldNotBeNull();
    }

    [Fact]
    public async Task Show_SingleEntry_ReturnsExpectedJson()
    {
        await SeedEntryAsync(runId: "run-1", rootId: 3260, workItemId: 3260, action: "branch_ensure_feature", target: "feature/3260", startedAt: 1_700_000_000_000);

        var (exitCode, output) = await CaptureConsoleAsync(() => _command.Show());
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalShowResult);

        exitCode.ShouldBe(ExitCodes.Success);
        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
        result.Entries[0].Action.ShouldBe("branch_ensure_feature");
        result.Entries[0].Target.ShouldBe("feature/3260");
        result.Entries[0].Outcome.ShouldBe(JournalOutcome.Success);
    }

    [Fact]
    public async Task Show_MultipleEntries_ReturnsEntriesInTimelineOrder()
    {
        await SeedEntryAsync(runId: "run-2", rootId: 3260, workItemId: 3260, action: "branch_ensure_feature", target: "feature/3260", startedAt: 2_000);
        await SeedEntryAsync(runId: "run-2", rootId: 3260, workItemId: 3261, action: "pr_open_feature", target: "https://example/pr/1", startedAt: 3_000);

        var (_, output) = await CaptureConsoleAsync(() => _command.Show());
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.JournalShowResult);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.Entries.Select(e => e.Action).ShouldBe(["branch_ensure_feature", "pr_open_feature"]);
    }

    [Fact]
    public async Task Show_FiltersByWorkItemRootAndAction()
    {
        await SeedEntryAsync(runId: "run-3", rootId: 3260, workItemId: 3260, action: "branch_ensure_feature", target: "feature/3260", startedAt: 1_000);
        await SeedEntryAsync(runId: "run-3", rootId: 3261, workItemId: 3260, action: "branch_ensure_feature", target: "feature/3261", startedAt: 2_000);
        await SeedEntryAsync(runId: "run-3", rootId: 3260, workItemId: 3262, action: "pr_open_feature", target: "https://example/pr/2", startedAt: 3_000);

        var (_, workItemJson) = await CaptureConsoleAsync(() => _command.Show(workItem: 3262));
        var workItemResult = JsonSerializer.Deserialize(workItemJson, PolyphonyJsonContext.Default.JournalShowResult);
        workItemResult.ShouldNotBeNull();
        workItemResult.Count.ShouldBe(1);
        workItemResult.Entries[0].WorkItemId.ShouldBe(3262);

        var (_, rootJson) = await CaptureConsoleAsync(() => _command.Show(root: 3261));
        var rootResult = JsonSerializer.Deserialize(rootJson, PolyphonyJsonContext.Default.JournalShowResult);
        rootResult.ShouldNotBeNull();
        rootResult.Count.ShouldBe(1);
        rootResult.Entries[0].RootId.ShouldBe(3261);

        var (_, actionJson) = await CaptureConsoleAsync(() => _command.Show(action: "pr_open_feature"));
        var actionResult = JsonSerializer.Deserialize(actionJson, PolyphonyJsonContext.Default.JournalShowResult);
        actionResult.ShouldNotBeNull();
        actionResult.Count.ShouldBe(1);
        actionResult.Entries[0].Action.ShouldBe("pr_open_feature");
    }

    [Fact]
    public async Task Show_TextRender_WritesTabularTimeline()
    {
        await SeedEntryAsync(runId: "run-4", rootId: 3260, workItemId: 3260, action: "branch_ensure_feature", target: "feature/3260", startedAt: 1_700_000_000_000);

        var (exitCode, output) = await CaptureConsoleAsync(() => _command.Show(render: "text"));

        exitCode.ShouldBe(ExitCodes.Success);
        output.ShouldContain("timestamp\taction\ttarget\toutcome\twork_item\troot");
        output.ShouldContain("branch_ensure_feature");
        output.ShouldContain("feature/3260");
        output.ShouldContain("success");
        output.ShouldContain("3260");
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

    private async Task SeedEntryAsync(string runId, int? rootId, int? workItemId, string action, string target, long startedAt)
    {
        var actionId = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = runId,
                RootId = rootId,
                WorkItemId = workItemId,
                Action = action,
                Target = target,
                StartedAt = startedAt,
            },
            CancellationToken.None);
        await _store.RecordEndAsync(actionId, JournalOutcome.Success, null, null, null, CancellationToken.None);
    }
}
