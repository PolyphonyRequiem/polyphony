using Polyphony.Infrastructure.Processes;
using Polyphony.Postconditions;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Postconditions;

/// <summary>
/// Unit tests for <see cref="PostconditionVerifier"/>. Covers all three
/// outcome cases (Satisfied / NeedsPush / Conflict), the fetch-failure
/// recovery path (treated as remote-missing), the show-failure recovery
/// path (treated as path-missing), and the empty-expectations vacuous
/// case.
/// </summary>
public sealed class PostconditionVerifierTests
{
    private static (PostconditionVerifier Verifier, FakeProcessRunner Runner) Create()
    {
        var runner = new FakeProcessRunner();
        return (new PostconditionVerifier(new GitClient(runner)), runner);
    }

    private static void StubFetch(FakeProcessRunner runner, string remote, string branch, ProcessResult? result = null)
        => runner.WhenExact("git", ["fetch", remote, branch],
            result ?? new ProcessResult(0, "", ""));

    private static void StubShow(FakeProcessRunner runner, string refSpec, string path, string? content)
    {
        var result = content is null
            ? new ProcessResult(128, "",
                $"fatal: path '{path}' does not exist in '{refSpec}'")
            : new ProcessResult(0, content, "");
        runner.WhenExact("git", ["show", $"{refSpec}:{path}"], result);
    }

    private static void StubShowFatal(FakeProcessRunner runner, string refSpec, string path, string stderr)
        => runner.WhenExact("git", ["show", $"{refSpec}:{path}"],
            new ProcessResult(128, "", stderr));

    // ─── Argument validation ────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_BlankBranch_Throws()
    {
        var (verifier, _) = Create();
        await Should.ThrowAsync<ArgumentException>(() =>
            verifier.VerifyAsync("", [new PostconditionExpectation("p", "x")]));
    }

    [Fact]
    public async Task VerifyAsync_BlankRemote_Throws()
    {
        var (verifier, _) = Create();
        await Should.ThrowAsync<ArgumentException>(() =>
            verifier.VerifyAsync("feature/100", [new PostconditionExpectation("p", "x")], remote: ""));
    }

    [Fact]
    public async Task VerifyAsync_NullExpectations_Throws()
    {
        var (verifier, _) = Create();
        await Should.ThrowAsync<ArgumentNullException>(() =>
            verifier.VerifyAsync("feature/100", null!));
    }

    // ─── Vacuous case ───────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_EmptyExpectations_SatisfiedWithoutFetch()
    {
        // Zero expectations are vacuously satisfied; no fetch needed.
        // Avoids a needless network call when the caller computed an
        // empty paths list defensively.
        var (verifier, runner) = Create();

        var outcome = await verifier.VerifyAsync("feature/100", []);

        outcome.ShouldBeOfType<PostconditionOutcome.Satisfied>();
        runner.Invocations.ShouldBeEmpty();
    }

    // ─── Satisfied ──────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_OriginMatchesAllExpectations_Satisfied()
    {
        var (verifier, runner) = Create();
        StubFetch(runner, "origin", "feature/100");
        StubShow(runner, "origin/feature/100", "a.md", "alpha");
        StubShow(runner, "origin/feature/100", "b.md", "beta");

        var outcome = await verifier.VerifyAsync(
            "feature/100",
            [
                new PostconditionExpectation("a.md", "alpha"),
                new PostconditionExpectation("b.md", "beta"),
            ]);

        outcome.ShouldBeOfType<PostconditionOutcome.Satisfied>();
    }

    [Fact]
    public async Task VerifyAsync_CustomRemote_UsesRemoteInRefSpec()
    {
        var (verifier, runner) = Create();
        StubFetch(runner, "upstream", "feature/100");
        StubShow(runner, "upstream/feature/100", "a.md", "alpha");

        var outcome = await verifier.VerifyAsync(
            "feature/100",
            [new PostconditionExpectation("a.md", "alpha")],
            remote: "upstream");

        outcome.ShouldBeOfType<PostconditionOutcome.Satisfied>();
    }

    // ─── NeedsPush ──────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_OriginMissingPath_NeedsPush()
    {
        var (verifier, runner) = Create();
        StubFetch(runner, "origin", "feature/100");
        StubShow(runner, "origin/feature/100", "a.md", content: null); // missing
        StubShow(runner, "origin/feature/100", "b.md", "beta");

        var outcome = await verifier.VerifyAsync(
            "feature/100",
            [
                new PostconditionExpectation("a.md", "alpha"),
                new PostconditionExpectation("b.md", "beta"),
            ]);

        var needsPush = outcome.ShouldBeOfType<PostconditionOutcome.NeedsPush>();
        needsPush.Paths.ShouldBe(["a.md"]);
    }

    [Fact]
    public async Task VerifyAsync_FetchFails_NeedsPushAllPaths()
    {
        // Fetch failure most often means "remote ref doesn't exist yet"
        // — first push of a fresh branch. Treat every fetch failure as
        // "everything missing"; if the remote is genuinely unreachable
        // the caller's subsequent push will fail loudly.
        var (verifier, runner) = Create();
        StubFetch(runner, "origin", "feature/100",
            new ProcessResult(128, "", "fatal: couldn't find remote ref feature/100"));

        var outcome = await verifier.VerifyAsync(
            "feature/100",
            [
                new PostconditionExpectation("a.md", "alpha"),
                new PostconditionExpectation("b.md", "beta"),
            ]);

        var needsPush = outcome.ShouldBeOfType<PostconditionOutcome.NeedsPush>();
        needsPush.Paths.ShouldBe(["a.md", "b.md"]);
        // No show calls should have been made — fetch shortcut.
        runner.Invocations.ShouldNotContain(c => c.Executable == "git" && c.Arguments[0] == "show");
    }

    [Fact]
    public async Task VerifyAsync_ShowFails_TreatedAsMissing()
    {
        // Belt-and-suspenders: if `git show` throws ExternalToolException
        // for an unrecognized stderr (e.g. "ambiguous argument"), treat
        // the path as missing rather than propagating.
        var (verifier, runner) = Create();
        StubFetch(runner, "origin", "feature/100");
        StubShowFatal(runner, "origin/feature/100", "a.md",
            "fatal: ambiguous argument 'origin/feature/100': unknown revision");

        var outcome = await verifier.VerifyAsync(
            "feature/100",
            [new PostconditionExpectation("a.md", "alpha")]);

        outcome.ShouldBeOfType<PostconditionOutcome.NeedsPush>();
    }

    // ─── Conflict ───────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_OriginContentDiffers_Conflict()
    {
        var (verifier, runner) = Create();
        StubFetch(runner, "origin", "feature/100");
        StubShow(runner, "origin/feature/100", "a.md", "alpha");
        StubShow(runner, "origin/feature/100", "b.md", "STALE_BETA");

        var outcome = await verifier.VerifyAsync(
            "feature/100",
            [
                new PostconditionExpectation("a.md", "alpha"),
                new PostconditionExpectation("b.md", "beta"),
            ]);

        var conflict = outcome.ShouldBeOfType<PostconditionOutcome.Conflict>();
        conflict.Conflicts.Count.ShouldBe(1);
        conflict.Conflicts[0].Path.ShouldBe("b.md");
        conflict.Conflicts[0].ExpectedContent.ShouldBe("beta");
        conflict.Conflicts[0].ActualContent.ShouldBe("STALE_BETA");
    }

    [Fact]
    public async Task VerifyAsync_ConflictAndMissing_ConflictWins()
    {
        // When the verifier sees both conflict and missing rows, Conflict
        // is the stronger signal (it implies "force-push or escalate";
        // a plain push won't fast-forward). Document that contract here.
        var (verifier, runner) = Create();
        StubFetch(runner, "origin", "feature/100");
        StubShow(runner, "origin/feature/100", "a.md", content: null);    // missing
        StubShow(runner, "origin/feature/100", "b.md", "STALE");          // conflict

        var outcome = await verifier.VerifyAsync(
            "feature/100",
            [
                new PostconditionExpectation("a.md", "alpha"),
                new PostconditionExpectation("b.md", "beta"),
            ]);

        outcome.ShouldBeOfType<PostconditionOutcome.Conflict>();
    }

    [Fact]
    public async Task VerifyAsync_ContentComparisonIsByteExact()
    {
        // Whitespace and line-ending differences are real conflicts.
        // The verifier doesn't normalize — git stores blobs verbatim and
        // so do we.
        var (verifier, runner) = Create();
        StubFetch(runner, "origin", "feature/100");
        StubShow(runner, "origin/feature/100", "a.md", "hello\n");

        var outcome = await verifier.VerifyAsync(
            "feature/100",
            [new PostconditionExpectation("a.md", "hello\r\n")]);

        outcome.ShouldBeOfType<PostconditionOutcome.Conflict>();
    }
}
