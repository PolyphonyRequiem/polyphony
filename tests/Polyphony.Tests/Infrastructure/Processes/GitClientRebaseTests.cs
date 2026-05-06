using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

/// <summary>
/// Unit tests for <see cref="GitClient.RebaseOntoAsync"/>. Drives
/// <see cref="FakeProcessRunner"/> rather than spawning real git so the
/// tests stay deterministic and fast — the goal is to pin the verb's
/// argument shape and its handling of clean / conflict / failed exit
/// codes, not to re-validate git itself.
/// </summary>
public sealed class GitClientRebaseTests
{
    [Fact]
    public async Task RebaseOnto_CleanRebase_ReturnsCleanWithNewSha()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["checkout", "--detach", "head-sha"], new ProcessResult(0, "", ""));
        fake.WhenExact("git", ["rebase", "--onto", "origin/plan/100", "old-base", "HEAD"], new ProcessResult(0, "Successfully rebased and updated HEAD.\n", ""));
        fake.WhenExact("git", ["rev-parse", "HEAD"], new ProcessResult(0, "new-head-sha\n", ""));
        var client = new GitClient(fake);

        var outcome = await client.RebaseOntoAsync("origin/plan/100", "old-base", "head-sha", default);

        var clean = outcome.ShouldBeOfType<RebaseOutcome.Clean>();
        clean.NewHeadSha.ShouldBe("new-head-sha");
        // Spawn order: checkout → rebase → rev-parse. No abort on the happy path.
        fake.Invocations.Count.ShouldBe(3);
    }

    [Fact]
    public async Task RebaseOnto_Conflict_ReturnsConflictWithFiles_AndAborts()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["checkout", "--detach", "head-sha"], new ProcessResult(0, "", ""));
        var rebaseStdout = "First, rewinding head to replay your work on top of it...\nApplying: change\nCONFLICT (content): Merge conflict in src/foo.cs\nCONFLICT (content): Merge conflict in src/bar.cs\nerror: Failed to merge in the changes.\n";
        fake.WhenExact("git", ["rebase", "--onto", "origin/plan/100", "old-base", "HEAD"], new ProcessResult(1, rebaseStdout, ""));
        fake.WhenExact("git", ["rebase", "--abort"], new ProcessResult(0, "", ""));
        var client = new GitClient(fake);

        var outcome = await client.RebaseOntoAsync("origin/plan/100", "old-base", "head-sha", default);

        var conflict = outcome.ShouldBeOfType<RebaseOutcome.Conflict>();
        conflict.Files.ShouldBe(["src/foo.cs", "src/bar.cs"]);
        // Must have run the abort.
        fake.Invocations.ShouldContain(i => i.Arguments.SequenceEqual(new[] { "rebase", "--abort" }));
    }

    [Fact]
    public async Task RebaseOnto_ConflictPath_DoesNotCallRevParse()
    {
        // On conflict we don't emit a NewHeadSha, so we must not call rev-parse.
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["checkout", "--detach", "head-sha"], new ProcessResult(0, "", ""));
        fake.WhenExact("git", ["rebase", "--onto", "newBase", "old-base", "HEAD"],
            new ProcessResult(1, "CONFLICT (content): Merge conflict in foo.txt\n", ""));
        fake.WhenExact("git", ["rebase", "--abort"], new ProcessResult(0, "", ""));
        var client = new GitClient(fake);

        await client.RebaseOntoAsync("newBase", "old-base", "head-sha", default);

        fake.Invocations.ShouldNotContain(i =>
            i.Arguments.Count == 2 && i.Arguments[0] == "rev-parse" && i.Arguments[1] == "HEAD");
    }

    [Fact]
    public async Task RebaseOnto_ConflictAbortAlsoFails_StillReturnsConflict()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["checkout", "--detach", "head-sha"], new ProcessResult(0, "", ""));
        fake.WhenExact("git", ["rebase", "--onto", "n", "o", "HEAD"],
            new ProcessResult(1, "CONFLICT (content): Merge conflict in foo.txt\n", ""));
        // Abort returns non-zero — should be swallowed (best-effort cleanup).
        fake.WhenExact("git", ["rebase", "--abort"], new ProcessResult(128, "", "fatal: no rebase in progress"));
        var client = new GitClient(fake);

        var outcome = await client.RebaseOntoAsync("n", "o", "head-sha", default);

        outcome.ShouldBeOfType<RebaseOutcome.Conflict>();
    }

    [Fact]
    public async Task RebaseOnto_FailedNotConflict_ReturnsFailedWithStderr_AndAborts()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["checkout", "--detach", "head-sha"], new ProcessResult(0, "", ""));
        fake.WhenExact("git", ["rebase", "--onto", "bogus-ref", "old-base", "HEAD"],
            new ProcessResult(128, "", "fatal: invalid upstream 'bogus-ref'"));
        fake.WhenExact("git", ["rebase", "--abort"], new ProcessResult(128, "", "fatal: no rebase in progress"));
        var client = new GitClient(fake);

        var outcome = await client.RebaseOntoAsync("bogus-ref", "old-base", "head-sha", default);

        var failed = outcome.ShouldBeOfType<RebaseOutcome.Failed>();
        failed.Stderr.ShouldContain("invalid upstream");
        // Abort still attempted defensively.
        fake.Invocations.ShouldContain(i => i.Arguments.SequenceEqual(new[] { "rebase", "--abort" }));
    }

    [Fact]
    public async Task RebaseOnto_CheckoutFails_ReturnsFailed_NoRebaseAttempted()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["checkout", "--detach", "head-sha"],
            new ProcessResult(1, "", "error: pathspec 'head-sha' did not match any file"));
        var client = new GitClient(fake);

        var outcome = await client.RebaseOntoAsync("origin/plan/100", "old-base", "head-sha", default);

        var failed = outcome.ShouldBeOfType<RebaseOutcome.Failed>();
        failed.Stderr.ShouldContain("did not match");
        // Only the checkout was attempted.
        fake.Invocations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RebaseOnto_RebaseSucceedsButRevParseFails_ReturnsFailed()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["checkout", "--detach", "head-sha"], new ProcessResult(0, "", ""));
        fake.WhenExact("git", ["rebase", "--onto", "n", "o", "HEAD"], new ProcessResult(0, "ok", ""));
        fake.WhenExact("git", ["rev-parse", "HEAD"], new ProcessResult(128, "", "fatal: ambiguous argument 'HEAD'"));
        var client = new GitClient(fake);

        var outcome = await client.RebaseOntoAsync("n", "o", "head-sha", default);

        var failed = outcome.ShouldBeOfType<RebaseOutcome.Failed>();
        failed.Stderr.ShouldContain("ambiguous argument");
    }

    [Fact]
    public async Task RebaseOnto_RejectsInvalidArgs()
    {
        var client = new GitClient(new FakeProcessRunner());
        await Should.ThrowAsync<ArgumentException>(async () => await client.RebaseOntoAsync("", "o", "h"));
        await Should.ThrowAsync<ArgumentException>(async () => await client.RebaseOntoAsync("n", "", "h"));
        await Should.ThrowAsync<ArgumentException>(async () => await client.RebaseOntoAsync("n", "o", ""));
    }

    [Fact]
    public async Task RebaseOnto_PassesArgumentsVerbatim()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["checkout", "--detach", "abcd1234"], new ProcessResult(0, "", ""));
        fake.WhenExact("git", ["rebase", "--onto", "origin/plan/100-200", "deadbeef", "HEAD"], new ProcessResult(0, "ok", ""));
        fake.WhenExact("git", ["rev-parse", "HEAD"], new ProcessResult(0, "newcommit\n", ""));
        var client = new GitClient(fake);

        var outcome = await client.RebaseOntoAsync("origin/plan/100-200", "deadbeef", "abcd1234", default);

        outcome.ShouldBeOfType<RebaseOutcome.Clean>().NewHeadSha.ShouldBe("newcommit");
    }

    [Fact]
    public async Task PushHeadWithLease_HappyPath_PassesFullRefAndLeaseSpec()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git",
            ["push", "origin", "HEAD:refs/heads/plan/100-200", "--force-with-lease=refs/heads/plan/100-200:abc123"],
            new ProcessResult(0, "", ""));
        var client = new GitClient(fake);

        var result = await client.PushHeadWithLeaseAsync("origin", "plan/100-200", "abc123", default);

        result.Succeeded.ShouldBeTrue();
        result.ExitCode.ShouldBe(0);
        // Single push invocation; no extra calls.
        fake.Invocations.Count.ShouldBe(1);
        fake.Invocations[0].Arguments.ShouldBe(
            ["push", "origin", "HEAD:refs/heads/plan/100-200", "--force-with-lease=refs/heads/plan/100-200:abc123"]);
    }

    [Fact]
    public async Task PushHeadWithLease_LeaseRejected_ReturnsRawProcessResultForCallerRouting()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git",
            ["push", "origin", "HEAD:refs/heads/plan/100-200", "--force-with-lease=refs/heads/plan/100-200:abc123"],
            new ProcessResult(1, "", "error: stale info: remote ref has changed; refusing to update"));
        var client = new GitClient(fake);

        var result = await client.PushHeadWithLeaseAsync("origin", "plan/100-200", "abc123", default);

        result.Succeeded.ShouldBeFalse();
        result.ExitCode.ShouldBe(1);
        result.Stderr.ShouldContain("stale info");
    }

    [Fact]
    public async Task PushHeadWithLease_RejectsInvalidArgs()
    {
        var client = new GitClient(new FakeProcessRunner());
        await Should.ThrowAsync<ArgumentException>(async () => await client.PushHeadWithLeaseAsync("", "b", "s"));
        await Should.ThrowAsync<ArgumentException>(async () => await client.PushHeadWithLeaseAsync("origin", "", "s"));
        await Should.ThrowAsync<ArgumentException>(async () => await client.PushHeadWithLeaseAsync("origin", "b", ""));
    }
}
