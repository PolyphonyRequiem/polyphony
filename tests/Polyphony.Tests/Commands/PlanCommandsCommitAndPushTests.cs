using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Postconditions;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

[Collection("CwdSerial")]
public sealed class PlanCommandsCommitAndPushTests : CommandTestBase, IDisposable
{
    private readonly string tempDir;

    public PlanCommandsCommitAndPushTests()
    {
        // Tests use relative pathspecs (e.g. "plans/plan-100.md"). The
        // verb reads file contents on the no-op path to drive the
        // verifier comparison; chdir into a temp dir so paths created
        // here don't leak into the repo. Don't capture previous cwd —
        // sibling test classes (e.g. ManifestCommandsCommitAndPushTests)
        // race on cwd in parallel runs and would have deleted theirs by
        // the time we restore.
        this.tempDir = Path.Combine(
            Path.GetTempPath(),
            "polyphony-plan-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);
        Directory.SetCurrentDirectory(this.tempDir);
    }

    public new void Dispose()
    {
        // Restore to a guaranteed-stable directory (the test binary
        // location) rather than a racy snapshot of cwd from ctor time.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
        base.Dispose();
    }

    private (PlanCommands Command, FakeProcessRunner Runner, FakePostconditionVerifier Verifier) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var verifier = new FakePostconditionVerifier();
        var git = new GitClient(runner);
        return (new PlanCommands(walker, Repository, Config, twig, git, new GhClient(runner), verifier, new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git)), runner, verifier);
    }

    private static PlanCommitAndPushResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanCommitAndPushResult)!;

    /// <summary>Writes a file under <c>tempDir</c> and returns the relative pathspec.</summary>
    private string WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(this.tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return relativePath;
    }

    // ─── Stubs ────────────────────────────────────────────────────────────

    private static void StubCurrentBranch(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["branch", "--show-current"],
            new ProcessResult(0, branch + "\n", ""));

    private static void StubCheckout(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", branch], new ProcessResult(0, "", ""));

    private static void StubAdd(FakeProcessRunner runner, string pathspec)
        => runner.WhenExact("git", ["add", "--", pathspec], new ProcessResult(0, "", ""));

    private static void StubAddFails(FakeProcessRunner runner, string pathspec, string stderr)
        => runner.WhenExact("git", ["add", "--", pathspec], new ProcessResult(128, "", stderr));

    private static void StubStatus(FakeProcessRunner runner, string porcelain)
        => runner.WhenExact("git", ["status", "--porcelain"], new ProcessResult(0, porcelain, ""));

    private static void StubCommit(FakeProcessRunner runner, string message)
        => runner.WhenExact("git", ["commit", "-m", message], new ProcessResult(0, "", ""));

    private static void StubPush(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubRevParse(FakeProcessRunner runner, string branch, string sha)
        => runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(0, sha + "\n", ""));

    // ─── Argument validation (Move #2 routing-style) ──────────────────────

    [Fact]
    public async Task CommitAndPush_BlankBranch_InvalidInputs()
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "", message: "msg", paths: "x.md"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_inputs");
        result.Error!.ShouldContain("branch is required");
    }

    [Fact]
    public async Task CommitAndPush_BlankMessage_InvalidInputs()
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "", paths: "x.md"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_inputs");
        result.Error!.ShouldContain("message is required");
    }

    [Fact]
    public async Task CommitAndPush_BlankPaths_InvalidInputs()
    {
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "msg", paths: ""));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_inputs");
        result.Error!.ShouldContain("paths is required");
    }

    [Fact]
    public async Task CommitAndPush_AllSentinelDefaults_InvalidInputs()
    {
        // Move #2 sentinel default — invoking the verb with no arguments
        // hits the in-body validation, not a framework-level error.
        var (cmd, _, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.CommitAndPush());
        exit.ShouldBe(ExitCodes.Success);
        Parse(output).ErrorCode.ShouldBe("invalid_inputs");
    }

    // ─── Happy paths ──────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_SinglePath_StagesCommitsPushes()
    {
        var (cmd, runner, verifier) = CreateCommand();
        var path = WriteFile("plans/plan-100.md", "# plan\nbody\n");
        StubCurrentBranch(runner, "plan/100");
        StubAdd(runner, path);
        // After staging, status shows the file as staged-modified ("M ").
        StubStatus(runner, $"M  {path}\n");
        StubCommit(runner, "plan: 100");
        StubPush(runner, "plan/100");
        StubRevParse(runner, "plan/100", "abc1234deadbeef");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Branch.ShouldBe("plan/100");
        result.Pushed.ShouldBeTrue();
        result.FilesStaged.ShouldBe(1);
        result.CommitSha.ShouldBe("abc1234deadbeef");
        result.NoOpReason.ShouldBeNull();
        result.Error.ShouldBeNull();
        // Commit path → verifier never consulted.
        verifier.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CommitAndPush_MultiplePaths_AllStaged()
    {
        var (cmd, runner, _) = CreateCommand();
        var p1 = WriteFile("plans/plan-101.md", "# plan 101\n");
        var p2 = WriteFile("plans/plan-101-supporting.md", "# supporting\n");
        StubCurrentBranch(runner, "plan/100-101");
        StubAdd(runner, p1);
        StubAdd(runner, p2);
        // Both files staged → two `M ` rows.
        StubStatus(runner, $"M  {p1}\nM  {p2}\n");
        StubCommit(runner, "plan: 101");
        StubPush(runner, "plan/100-101");
        StubRevParse(runner, "plan/100-101", "deadbeefabc1234");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(
                branch: "plan/100-101",
                message: "plan: 101",
                paths: $"{p1}, {p2}"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Pushed.ShouldBeTrue();
        result.FilesStaged.ShouldBe(2);
        result.CommitSha.ShouldBe("deadbeefabc1234");
    }

    [Fact]
    public async Task CommitAndPush_OnDifferentBranch_ChecksOutFirst()
    {
        var (cmd, runner, _) = CreateCommand();
        var path = WriteFile("plans/plan-100.md", "# plan\n");
        StubCurrentBranch(runner, "main");
        StubCheckout(runner, "plan/100");
        StubAdd(runner, path);
        StubStatus(runner, $"M  {path}\n");
        StubCommit(runner, "plan: 100");
        StubPush(runner, "plan/100");
        StubRevParse(runner, "plan/100", "abc1234");

        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: path));

        exit.ShouldBe(ExitCodes.Success);
        runner.Invocations.ShouldContain(c => c.Executable == "git" && c.Arguments.SequenceEqual(new[] { "checkout", "plan/100" }));
    }

    // ─── No-op idempotency: origin agrees ────────────────────────────────

    [Fact]
    public async Task CommitAndPush_NothingStaged_OriginSatisfied_NoOpSuccess()
    {
        var (cmd, runner, verifier) = CreateCommand();
        var path = WriteFile("plans/plan-100.md", "# plan\n");
        StubCurrentBranch(runner, "plan/100");
        StubAdd(runner, path);
        // Status shows nothing staged — file is already committed at HEAD.
        StubStatus(runner, "");
        // Verifier reports satisfied — origin already has the blob.
        verifier.NextOutcome = new PostconditionOutcome.Satisfied();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Pushed.ShouldBeFalse();
        result.FilesStaged.ShouldBe(0);
        result.NoOpReason.ShouldBe("no_changes");
        result.CommitSha.ShouldBeNull();
        result.Error.ShouldBeNull();

        // No commit + push were executed.
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "commit");
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "push");

        // Verifier MUST have been consulted with the on-disk content.
        verifier.Calls.Count.ShouldBe(1);
        verifier.Calls[0].Branch.ShouldBe("plan/100");
        verifier.Calls[0].Expectations.Count.ShouldBe(1);
        verifier.Calls[0].Expectations[0].Path.ShouldBe(path);
        verifier.Calls[0].Expectations[0].ExpectedContent.ShouldBe("# plan\n");
    }

    [Fact]
    public async Task CommitAndPush_OnlyUnstagedChanges_OriginSatisfied_NoOpSuccess()
    {
        var (cmd, runner, verifier) = CreateCommand();
        var path = WriteFile("plans/plan-100.md", "# plan\n");
        StubCurrentBranch(runner, "plan/100");
        StubAdd(runner, path);
        // Untracked + unstaged-modified files should not trigger a commit.
        // (First column space = unstaged; '?' = untracked.)
        StubStatus(runner, " M other-file.txt\n?? scratch.md\n");
        verifier.NextOutcome = new PostconditionOutcome.Satisfied();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Pushed.ShouldBeFalse();
        result.NoOpReason.ShouldBe("no_changes");
    }

    // ─── Remote-side guard (Class B parity with manifest verb) ───────────

    [Fact]
    public async Task CommitAndPush_LocalCleanButOriginMissing_PushesAnyway()
    {
        // Class B parity with the manifest verb's #192 fix: local HEAD has
        // the plan (so `git add` stages nothing), but origin lacks it
        // (e.g. a prior run committed locally but never pushed). Without
        // this guard, the verb would silently no-op and downstream verbs
        // would fail when reading the plan from origin.
        var (cmd, runner, verifier) = CreateCommand();
        var path = WriteFile("plans/plan-100.md", "# plan body\n");
        StubCurrentBranch(runner, "plan/100");
        StubAdd(runner, path);
        StubStatus(runner, "");
        verifier.NextOutcome = new PostconditionOutcome.NeedsPush([path]);
        StubPush(runner, "plan/100");
        StubRevParse(runner, "plan/100", "deadbeef");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Pushed.ShouldBeTrue();
        result.NoOpReason.ShouldBeNull();
        result.CommitSha.ShouldBe("deadbeef");
        result.ErrorCode.ShouldBeNull();
        // Critical: no commit — HEAD already had the right blob.
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "commit");
        runner.Invocations.ShouldContain(c =>
            c.Executable == "git" &&
            c.Arguments.SequenceEqual(new[] { "push", "-u", "origin", "plan/100" }));
    }

    [Fact]
    public async Task CommitAndPush_LocalCleanButOriginConflict_PushesAnyway()
    {
        // Conflict variant: origin has different content. Verb pushes
        // (let git reject as non-fast-forward → git_failed if it does).
        var (cmd, runner, verifier) = CreateCommand();
        var path = WriteFile("plans/plan-100.md", "# new plan\n");
        StubCurrentBranch(runner, "plan/100");
        StubAdd(runner, path);
        StubStatus(runner, "");
        verifier.NextOutcome = new PostconditionOutcome.Conflict(
            [new PostconditionConflict(path, "# new plan\n", "# OLD\n")]);
        StubPush(runner, "plan/100");
        StubRevParse(runner, "plan/100", "abc123");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Pushed.ShouldBeTrue();
        result.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task CommitAndPush_LocalCleanRemotePushFails_GitFailed()
    {
        // Recovery push can still fail (non-fast-forward). Surface as
        // git_failed rather than masking the failure.
        var (cmd, runner, verifier) = CreateCommand();
        var path = WriteFile("plans/plan-100.md", "# plan\n");
        StubCurrentBranch(runner, "plan/100");
        StubAdd(runner, path);
        StubStatus(runner, "");
        verifier.NextOutcome = new PostconditionOutcome.NeedsPush([path]);
        runner.WhenExact("git", ["push", "-u", "origin", "plan/100"],
            new ProcessResult(1, "", "fatal: non-fast-forward"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("git_failed");
        result.Error!.ShouldContain("non-fast-forward");
        result.Pushed.ShouldBeFalse();
    }

    // ─── Failures ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_AddFails_GitFailed()
    {
        var (cmd, runner, _) = CreateCommand();
        StubCurrentBranch(runner, "plan/100");
        StubAddFails(runner, "plans/missing.md", "fatal: pathspec 'plans/missing.md' did not match any files");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: "plans/missing.md"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("git_failed");
        result.Pushed.ShouldBeFalse();
        result.Error!.ShouldContain("did not match any files");
    }

    [Fact]
    public async Task CommitAndPush_CheckoutFails_GitFailed()
    {
        var (cmd, runner, _) = CreateCommand();
        StubCurrentBranch(runner, "main");
        runner.WhenExact("git", ["checkout", "plan/100"],
            new ProcessResult(1, "", "error: pathspec 'plan/100' did not match any file(s) known to git"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.CommitAndPush(branch: "plan/100", message: "plan: 100", paths: "plans/plan-100.md"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("git_failed");
        result.Error!.ShouldContain("did not match any file");
    }
}
