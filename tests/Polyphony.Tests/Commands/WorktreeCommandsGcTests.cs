using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class WorktreeCommandsGcTests : CommandTestBase
{
    private readonly string _tempRoot;
    private readonly string _runsRoot;
    private readonly string _bareDir;

    public WorktreeCommandsGcTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(),
            $"poly-gc-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        // Layout the gc verb expects:
        //   <_tempRoot>/repo.git/      ← bare common-dir
        //   <_tempRoot>/repo-runs/     ← derived runs root
        _bareDir = Path.Combine(_tempRoot, "repo.git");
        _runsRoot = Path.Combine(_tempRoot, "repo-runs");
        Directory.CreateDirectory(_bareDir);
        Directory.CreateDirectory(_runsRoot);
    }

    public override void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch { /* best-effort */ }
        }
        base.Dispose();
    }

    private (WorktreeCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        // Common-dir resolves to the bare repo for every test.
        runner.WhenExact("git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, _bareDir + Environment.NewLine, ""));
        return (new WorktreeCommands(new GitClient(runner)), runner);
    }

    private static WorktreeGcResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorktreeGcResult)!;

    private string PorcelainEntry(string path, string sha, string branch) =>
        $"worktree {path}\nHEAD {sha}\nbranch refs/heads/{branch}\n\n";

    // ─── Resolution failures ──────────────────────────────────────────────

    [Fact]
    public async Task Gc_NoCommonDir_RoutesViaErrorField()
    {
        var runner = new FakeProcessRunner();
        runner.WhenExact("git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(128, "", "fatal: not a git repository\n"));
        var cmd = new WorktreeCommands(new GitClient(runner));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("Not inside a git repository");
        result.Candidates.ShouldBeEmpty();
        result.DryRun.ShouldBeTrue();
    }

    [Fact]
    public async Task Gc_GitWorktreeListFails_RoutesViaErrorField()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(128, "", "fatal: bad index file\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("bad index file");
        result.RunsRoot.ShouldBe(_runsRoot);
    }

    // ─── Empty / no-op scans ──────────────────────────────────────────────

    [Fact]
    public async Task Gc_EmptyWorktreeList_NoCandidates()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Candidates.ShouldBeEmpty();
        result.RemovedCount.ShouldBe(0);
        result.FailedCount.ShouldBe(0);
        result.DryRun.ShouldBeTrue();
        result.Apex.ShouldBe(0);
        result.RunsRoot.ShouldBe(_runsRoot);
    }

    [Fact]
    public async Task Gc_OnlyMainWorktreeOutsideRunsRoot_Skipped()
    {
        var (cmd, runner) = CreateCommand();
        var mainPath = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(mainPath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(mainPath, "abc", "main"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Candidates.ShouldBeEmpty();
        result.RemovedCount.ShouldBe(0);
    }

    // ─── Prune-reason classification (dry-run) ────────────────────────────

    [Fact]
    public async Task Gc_DryRun_DirectoryMissing_ReportedAsCandidate()
    {
        var (cmd, runner) = CreateCommand();
        // Path under runs_root that does NOT exist on disk.
        var prunePath = Path.Combine(_runsRoot, "apex-1", "feature-1");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(prunePath, "abc", "feature/1"), ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Candidates.Count.ShouldBe(1);
        var cand = result.Candidates[0];
        cand.Reason.ShouldBe("directory_missing");
        cand.WouldRemove.ShouldBeTrue();
        cand.Removed.ShouldBeFalse();
        cand.Branch.ShouldBe("feature/1");
        result.RemovedCount.ShouldBe(1);
        result.FailedCount.ShouldBe(0);

        // Dry run MUST NOT call git worktree remove.
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "worktree" && i.Arguments[1] == "remove");
    }

    [Fact]
    public async Task Gc_DryRun_BranchDeleted_ReportedAsCandidate()
    {
        var (cmd, runner) = CreateCommand();
        var prunePath = Path.Combine(_runsRoot, "apex-1", "impl-1-99");
        Directory.CreateDirectory(prunePath); // dir exists; branch missing.
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(prunePath, "abc", "impl/1-99"), ""));
        // Branch lookup returns failure (branch gone).
        runner.WhenExact("git",
            ["rev-parse", "--verify", "refs/heads/impl/1-99"],
            new ProcessResult(128, "", "fatal: Needed a single revision\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Candidates.Count.ShouldBe(1);
        result.Candidates[0].Reason.ShouldBe("branch_deleted");
        result.Candidates[0].WouldRemove.ShouldBeTrue();
    }

    [Fact]
    public async Task Gc_DryRun_BranchStillExists_Skipped()
    {
        var (cmd, runner) = CreateCommand();
        var keepPath = Path.Combine(_runsRoot, "apex-1", "feature-1");
        Directory.CreateDirectory(keepPath);
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(keepPath, "abc", "feature/1"), ""));
        // Branch still resolves.
        runner.WhenExact("git",
            ["rev-parse", "--verify", "refs/heads/feature/1"],
            new ProcessResult(0, "abcdef\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Candidates.ShouldBeEmpty();
        result.RemovedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Gc_MixedScan_ReportsOnlyPrunable()
    {
        var (cmd, runner) = CreateCommand();
        var keepPath = Path.Combine(_runsRoot, "apex-1", "feature-1");
        var prunePath = Path.Combine(_runsRoot, "apex-1", "impl-1-99");
        var outsidePath = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(keepPath);
        Directory.CreateDirectory(outsidePath);
        // prunePath is intentionally NOT created (directory_missing)

        var porcelain =
            PorcelainEntry(outsidePath, "111", "main") +
            PorcelainEntry(keepPath, "222", "feature/1") +
            PorcelainEntry(prunePath, "333", "impl/1-99");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, porcelain, ""));
        runner.WhenExact("git",
            ["rev-parse", "--verify", "refs/heads/feature/1"],
            new ProcessResult(0, "abcdef\n", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Candidates.Count.ShouldBe(1);
        result.Candidates[0].Path.ShouldBe(prunePath);
        result.Candidates[0].Reason.ShouldBe("directory_missing");
    }

    // ─── --apex scope ─────────────────────────────────────────────────────

    [Fact]
    public async Task Gc_ApexScope_FiltersOutOtherApexes()
    {
        var (cmd, runner) = CreateCommand();
        var apex1Path = Path.Combine(_runsRoot, "apex-1", "impl-1-99"); // missing → prunable
        var apex2Path = Path.Combine(_runsRoot, "apex-2", "impl-2-99"); // missing → would be prunable, but out of scope

        var porcelain =
            PorcelainEntry(apex1Path, "111", "impl/1-99") +
            PorcelainEntry(apex2Path, "222", "impl/2-99");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, porcelain, ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc(apex: 1));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Apex.ShouldBe(1);
        result.Candidates.Count.ShouldBe(1);
        result.Candidates[0].Path.ShouldBe(apex1Path);
    }

    // ─── --commit (real removal) ──────────────────────────────────────────

    [Fact]
    public async Task Gc_Commit_RemovesPrunable()
    {
        var (cmd, runner) = CreateCommand();
        var prunePath = Path.Combine(_runsRoot, "apex-1", "feature-1"); // missing
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(prunePath, "abc", "feature/1"), ""));
        runner.WhenExact("git", ["worktree", "remove", "--force", prunePath],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc(commit: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.DryRun.ShouldBeFalse();
        result.Candidates.Count.ShouldBe(1);
        result.Candidates[0].Removed.ShouldBeTrue();
        result.Candidates[0].WouldRemove.ShouldBeFalse();
        result.RemovedCount.ShouldBe(1);
        result.FailedCount.ShouldBe(0);

        runner.Invocations.ShouldContain(i =>
            i.Arguments.SequenceEqual(new[] { "worktree", "remove", "--force", prunePath }));
    }

    [Fact]
    public async Task Gc_Commit_RemoveFailure_RecordedAsFailed()
    {
        var (cmd, runner) = CreateCommand();
        var prunePath = Path.Combine(_runsRoot, "apex-1", "feature-1");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(prunePath, "abc", "feature/1"), ""));
        runner.WhenExact("git", ["worktree", "remove", "--force", prunePath],
            new ProcessResult(128, "", "fatal: file is locked\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc(commit: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Candidates.Count.ShouldBe(1);
        result.Candidates[0].Removed.ShouldBeFalse();
        result.Candidates[0].Error!.ShouldContain("file is locked");
        result.RemovedCount.ShouldBe(0);
        result.FailedCount.ShouldBe(1);
    }

    // ─── Safety: never touches main worktree even when listed under runs_root ─

    [Fact]
    public async Task Gc_NeverTouchesPathsOutsideRunsRoot()
    {
        var (cmd, runner) = CreateCommand();
        // A path under the bare repo's parent (NOT under runs_root) — should be skipped.
        var mainPath = Path.Combine(_tempRoot, "repo");
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, PorcelainEntry(mainPath, "abc", "main"), ""));
        // No worktree-remove responder registered: if the verb tried to remove
        // anything, the FakeProcessRunner would throw.

        var (exit, output) = await CaptureConsoleAsync(() => cmd.Gc(commit: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Candidates.ShouldBeEmpty();
        runner.Invocations.ShouldNotContain(i =>
            i.Arguments.Count >= 2 && i.Arguments[0] == "worktree" && i.Arguments[1] == "remove");
    }
}
