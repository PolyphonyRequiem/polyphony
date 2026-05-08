using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Polyphony.Postconditions;
using Polyphony.Routing;
using Polyphony.Tests.Commands;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// Tests for <c>polyphony manifest commit-and-push</c> — the verb that
/// closes the structural manifest-lifecycle gap surfaced by bug #12.
/// Routing-style: every error path exits 0 with a populated
/// <c>error_code</c>; the workflow never sees a non-zero exit.
///
/// <para>Wires the real <see cref="PostconditionVerifier"/> against the
/// shared <see cref="FakeProcessRunner"/> so the test exercises the full
/// fetch + show plumbing rather than a fake. This catches drift in the
/// "no_op vs needs_push" decision that the verifier owns post-Move #3.</para>
/// </summary>
[Collection("CwdSerial")]
public sealed class ManifestCommandsCommitAndPushTests : IDisposable
{
    private readonly string tempDir;
    private readonly string manifestPath;

    public ManifestCommandsCommitAndPushTests()
    {
        this.tempDir = Path.Combine(
            Path.GetTempPath(),
            "polyphony-manifest-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);
        // The verb defaults --path to ".polyphony/run.yaml" relative to
        // the current directory. We chdir into a temp dir so the
        // default-path branch can be exercised without leaking files into
        // the repo. Each test that relies on default path explicitly
        // creates the file under .polyphony/. Don't capture previous cwd
        // — sibling test classes (e.g. PlanCommandsCommitAndPushTests)
        // race on cwd in parallel runs and would have deleted theirs by
        // the time we restore.
        Directory.SetCurrentDirectory(this.tempDir);
        this.manifestPath = Path.Combine(this.tempDir, "run.yaml");
    }

    public void Dispose()
    {
        // Restore to a guaranteed-stable directory (the test binary
        // location) rather than a racy snapshot of cwd from ctor time.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private (ManifestCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var git = new GitClient(runner);
        return (new ManifestCommands(git, new PostconditionVerifier(git)), runner);
    }

    private static ManifestCommitAndPushResult Parse(string output) =>
        JsonSerializer.Deserialize(
            output,
            PolyphonyJsonContext.Default.ManifestCommitAndPushResult)!;

    /// <summary>Writes a valid manifest YAML to <paramref name="path"/> with the supplied root id.</summary>
    private static void WriteManifest(string path, int rootId)
    {
        var manifest = new RunManifest
        {
            Schema = RunManifestValidator.SupportedSchema,
            RootId = rootId,
            PlatformProject = "dev.azure.com/test-org/test-project",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "test",
            BranchModelVersion = RunManifestValidator.SupportedBranchModelVersion,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RunManifestStore.Save(path, manifest);
    }

    private static void StubCurrentBranch(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["branch", "--show-current"],
            new ProcessResult(0, branch + "\n", ""));

    private static void StubAdd(FakeProcessRunner runner, string pathspec)
        => runner.WhenExact("git", ["add", "--", pathspec], new ProcessResult(0, "", ""));

    private static void StubStatus(FakeProcessRunner runner, string porcelain)
        => runner.WhenExact("git", ["status", "--porcelain"], new ProcessResult(0, porcelain, ""));

    private static void StubCommit(FakeProcessRunner runner, string message)
        => runner.WhenExact("git", ["commit", "-m", message], new ProcessResult(0, "", ""));

    private static void StubPush(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubRevParse(FakeProcessRunner runner, string branch, string sha)
        => runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(0, sha + "\n", ""));

    private static void StubFetch(FakeProcessRunner runner, string branch, ProcessResult? result = null)
        => runner.WhenExact("git", ["fetch", "origin", branch],
            result ?? new ProcessResult(0, "", ""));

    /// <summary>Stubs <c>git show origin/{branch}:{path}</c> with the supplied content (null = ref/path missing).</summary>
    private static void StubShowOriginFile(FakeProcessRunner runner, string branch, string path, string? content)
    {
        var result = content is null
            ? new ProcessResult(128, "",
                $"fatal: path '{path}' does not exist in 'origin/{branch}'")
            : new ProcessResult(0, content, "");
        runner.WhenExact("git", ["show", $"origin/{branch}:{path}"], result);
    }

    /// <summary>Stubs <c>git show origin/{branch}:{path}</c> as a hard failure (e.g., remote ref missing entirely).</summary>
    private static void StubShowOriginFileFatal(FakeProcessRunner runner, string branch, string path, string stderr)
        => runner.WhenExact("git", ["show", $"origin/{branch}:{path}"],
            new ProcessResult(128, "", stderr));

    /// <summary>Reads the on-disk manifest content as the verifier would compare against origin.</summary>
    private static string ReadManifestContent(string path) => File.ReadAllText(path);

    // ─── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_ZeroRootId_InvalidInputs()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 0, path: this.manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_inputs");
        result.Error!.ShouldContain("--root-id must be positive");
        result.Pushed.ShouldBeFalse();
    }

    [Fact]
    public async Task CommitAndPush_NegativeRootId_InvalidInputs()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: -42, path: this.manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        Parse(output).ErrorCode.ShouldBe("invalid_inputs");
    }

    [Fact]
    public async Task CommitAndPush_DefaultRootIdSentinel_InvalidInputs()
    {
        // Move #2 sentinel default — invoking the verb with no --root-id
        // hits the in-body validation, not a framework-level error.
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(path: this.manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        Parse(output).ErrorCode.ShouldBe("invalid_inputs");
    }

    // ─── File state validation ───────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_ManifestMissing_ManifestMissing()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("manifest_missing");
        result.Branch.ShouldBe("feature/100");
        result.Pushed.ShouldBeFalse();
    }

    [Fact]
    public async Task CommitAndPush_ManifestUnparseable_ManifestParseFailed()
    {
        File.WriteAllText(this.manifestPath, "this: is: not: valid: yaml: ::::");
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));
        exit.ShouldBe(ExitCodes.Success);
        Parse(output).ErrorCode.ShouldBe("manifest_parse_failed");
    }

    [Fact]
    public async Task CommitAndPush_RootIdMismatch_ManifestRootMismatch()
    {
        // Manifest is for root 999 but caller asks for root 100.
        WriteManifest(this.manifestPath, rootId: 999);

        var (cmd, _) = CreateCommand();
        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("manifest_root_mismatch");
        result.Error!.ShouldContain("root_id=999");
        result.Error!.ShouldContain("--root-id=100");
    }

    // ─── Branch validation ───────────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_OnWrongBranch_WrongBranch()
    {
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "main");

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("wrong_branch");
        result.Error!.ShouldContain("worktree is on 'main'");
        result.Error!.ShouldContain("expected 'feature/100'");

        // Verb refused — no stage/commit/push attempted.
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "add");
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "commit");
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "push");
    }

    [Fact]
    public async Task CommitAndPush_DetachedHead_WrongBranch()
    {
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        // git branch --show-current returns empty when detached.
        runner.WhenExact("git", ["branch", "--show-current"], new ProcessResult(0, "\n", ""));

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("wrong_branch");
        result.Error!.ShouldContain("detached");
    }

    // ─── Happy paths ─────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_HappyPath_StagesCommitsPushes()
    {
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/100");
        StubAdd(runner, this.manifestPath);
        // Status row shows the manifest staged-as-added (`A `).
        StubStatus(runner, $"A  {this.manifestPath.Replace('\\', '/')}\n");
        StubCommit(runner, "manifest: bootstrap (root 100)");
        StubPush(runner, "feature/100");
        StubRevParse(runner, "feature/100", "feedfacedeadbeef");

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeNull();
        result.Error.ShouldBeNull();
        result.Pushed.ShouldBeTrue();
        result.RootId.ShouldBe(100);
        result.Branch.ShouldBe("feature/100");
        result.CommitSha.ShouldBe("feedfacedeadbeef");
        result.NoOpReason.ShouldBeNull();
    }

    [Fact]
    public async Task CommitAndPush_CustomMessage_UsesSuppliedMessage()
    {
        WriteManifest(this.manifestPath, rootId: 200);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/200");
        StubAdd(runner, this.manifestPath);
        StubStatus(runner, $"M  {this.manifestPath.Replace('\\', '/')}\n");
        StubCommit(runner, "manifest: record post-rebase");
        StubPush(runner, "feature/200");
        StubRevParse(runner, "feature/200", "abc123");

        var (exit, _) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(
                rootId: 200,
                message: "manifest: record post-rebase",
                path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        runner.Invocations.ShouldContain(c =>
            c.Executable == "git" &&
            c.Arguments.SequenceEqual(new[] { "commit", "-m", "manifest: record post-rebase" }));
    }

    // ─── Idempotency: origin agrees → genuine no-op ─────────────────────

    [Fact]
    public async Task CommitAndPush_NothingStaged_OriginHasManifest_NoOpSuccess()
    {
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/100");
        StubAdd(runner, this.manifestPath);
        // Status empty — manifest already at HEAD on this branch.
        StubStatus(runner, "");
        // Origin has the manifest at the same content → genuine no-op
        // (issue #192 guard is satisfied).
        StubFetch(runner, "feature/100");
        StubShowOriginFile(runner, "feature/100", this.manifestPath, ReadManifestContent(this.manifestPath));

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeNull();
        result.Pushed.ShouldBeFalse();
        result.NoOpReason.ShouldBe("no_changes");
        result.CommitSha.ShouldBeNull();

        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "commit");
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "push");
    }

    [Fact]
    public async Task CommitAndPush_OnlyUnstagedChanges_OriginHasManifest_NoOpSuccess()
    {
        // Some other file has been modified but the manifest itself is
        // not staged. The verb must NOT commit those unrelated changes.
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/100");
        StubAdd(runner, this.manifestPath);
        StubStatus(runner, " M unrelated.txt\n?? scratch.md\n");
        StubFetch(runner, "feature/100");
        StubShowOriginFile(runner, "feature/100", this.manifestPath, ReadManifestContent(this.manifestPath));

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.NoOpReason.ShouldBe("no_changes");
        result.Pushed.ShouldBeFalse();
    }

    [Fact]
    public async Task CommitAndPush_OtherFileStagedButNotManifest_OriginHasManifest_NoOpSuccess()
    {
        // Defensive: even if a sibling file is staged, the verb must not
        // commit it on the manifest's behalf. Only the manifest entry in
        // the porcelain output authorizes a commit.
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/100");
        StubAdd(runner, this.manifestPath);
        StubStatus(runner, "M  some/other/file.txt\n");
        StubFetch(runner, "feature/100");
        StubShowOriginFile(runner, "feature/100", this.manifestPath, ReadManifestContent(this.manifestPath));

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.NoOpReason.ShouldBe("no_changes");
        result.Pushed.ShouldBeFalse();
        // Critical: no commit for someone else's staged change.
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "commit");
    }

    // ─── Remote-side guard (issue #192, absorbed from PR #193) ──────────

    [Fact]
    public async Task CommitAndPush_LocalCleanButOriginLacksManifest_PushesAnyway()
    {
        // The bug from issue #192: local HEAD has the manifest (so `git add`
        // stages nothing), but origin/{branch}:{path} is missing — e.g. a
        // prior run committed but never pushed, or the branch was created
        // before the workflow learned to push the manifest. Without the
        // post-condition check, we'd silently no-op and downstream verbs
        // (pr open-plan-pr) would fail when reading the manifest from origin.
        WriteManifest(this.manifestPath, rootId: 3043);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/3043");
        StubAdd(runner, this.manifestPath);
        StubStatus(runner, ""); // local clean — manifest matches HEAD
        StubFetch(runner, "feature/3043");
        StubShowOriginFile(runner, "feature/3043", this.manifestPath, content: null); // origin lacks it
        StubPush(runner, "feature/3043");
        StubRevParse(runner, "feature/3043", "deadbeefcafe1234");

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 3043, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeNull();
        result.Pushed.ShouldBeTrue();
        result.NoOpReason.ShouldBeNull(); // not a no-op — we recovered by pushing
        result.Branch.ShouldBe("feature/3043");
        result.CommitSha.ShouldBe("deadbeefcafe1234");

        // Critical: we must NOT have created a redundant commit; HEAD already had the right blob.
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "commit");
        // We MUST have pushed.
        runner.Invocations.ShouldContain(c =>
            c.Executable == "git" &&
            c.Arguments.SequenceEqual(new[] { "push", "-u", "origin", "feature/3043" }));
    }

    [Fact]
    public async Task CommitAndPush_LocalCleanAndOriginBranchMissingEntirely_PushesAnyway()
    {
        // Variant of the above: not just the manifest, the entire remote
        // branch is missing (first push of feature/{root}). Fetch will
        // fail with "couldn't find remote ref"; the verifier tolerates it
        // and pushes HEAD to create the remote branch.
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/100");
        StubAdd(runner, this.manifestPath);
        StubStatus(runner, "");
        // Fetch fails because origin doesn't have feature/100 yet.
        StubFetch(runner, "feature/100",
            new ProcessResult(128, "", "fatal: couldn't find remote ref feature/100"));
        StubPush(runner, "feature/100");
        StubRevParse(runner, "feature/100", "abc123");

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBeNull();
        result.Pushed.ShouldBeTrue();
        result.CommitSha.ShouldBe("abc123");
    }

    [Fact]
    public async Task CommitAndPush_LocalCleanAndShowFails_PushesAnyway()
    {
        // Belt-and-suspenders for ShowFileAtRefAsync stderr we don't
        // explicitly recognize: any ExternalToolException from the remote
        // check is treated as "origin lacks the manifest" and we push.
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/100");
        StubAdd(runner, this.manifestPath);
        StubStatus(runner, "");
        StubFetch(runner, "feature/100");
        StubShowOriginFileFatal(runner, "feature/100", this.manifestPath,
            "fatal: ambiguous argument 'origin/feature/100': unknown revision");
        StubPush(runner, "feature/100");
        StubRevParse(runner, "feature/100", "abc123");

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Pushed.ShouldBeTrue();
        result.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task CommitAndPush_LocalCleanAndOriginPushFails_GitFailed()
    {
        // Recovery push can still fail (e.g. non-fast-forward because
        // someone moved origin in the meantime). Surface as git_failed
        // rather than masking the failure.
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/100");
        StubAdd(runner, this.manifestPath);
        StubStatus(runner, "");
        StubFetch(runner, "feature/100");
        StubShowOriginFile(runner, "feature/100", this.manifestPath, content: null);
        runner.WhenExact("git", ["push", "-u", "origin", "feature/100"],
            new ProcessResult(1, "", "fatal: non-fast-forward"));

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("git_failed");
        result.Error!.ShouldContain("non-fast-forward");
        result.Pushed.ShouldBeFalse();
    }

    [Fact]
    public async Task CommitAndPush_LocalCleanButOriginContentDiffers_PushesAnyway()
    {
        // Conflict path: origin has the file at the path, but with
        // different content from local HEAD. The verifier surfaces this
        // as Conflict; the manifest verb treats Conflict the same as
        // NeedsPush — push and let git reject as non-fast-forward if it
        // truly is divergent. Future consumers can branch on Conflict.
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/100");
        StubAdd(runner, this.manifestPath);
        StubStatus(runner, "");
        StubFetch(runner, "feature/100");
        // Origin has SOMETHING at the path, but it's not our content.
        StubShowOriginFile(runner, "feature/100", this.manifestPath,
            "schema: 1\nroot_id: 999\n# stale older manifest\n");
        StubPush(runner, "feature/100");
        StubRevParse(runner, "feature/100", "abc123");

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Pushed.ShouldBeTrue();
        result.ErrorCode.ShouldBeNull();
        // No commit — HEAD already has the content we want.
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "commit");
    }

    // ─── Git failures ────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAndPush_PushFails_GitFailed()
    {
        WriteManifest(this.manifestPath, rootId: 100);
        var (cmd, runner) = CreateCommand();
        StubCurrentBranch(runner, "feature/100");
        StubAdd(runner, this.manifestPath);
        StubStatus(runner, $"A  {this.manifestPath.Replace('\\', '/')}\n");
        StubCommit(runner, "manifest: bootstrap (root 100)");
        runner.WhenExact("git", ["push", "-u", "origin", "feature/100"],
            new ProcessResult(1, "", "fatal: unable to access remote"));

        var (exit, output) = await CommandTestBase_CaptureAsync(
            () => cmd.CommitAndPush(rootId: 100, path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("git_failed");
        result.Error!.ShouldContain("unable to access remote");
        result.Pushed.ShouldBeFalse();
    }

    // ─── Stdout-capture wrapper (mirrors ManifestCommandsTests) ─────────

    private static async Task<(int ExitCode, string Output)> CommandTestBase_CaptureAsync(Func<Task<int>> action)
    {
        await ConsoleTestLock.AsyncLock.WaitAsync();
        try
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var exit = await action();
                return (exit, writer.ToString().Trim());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }
    }
}
