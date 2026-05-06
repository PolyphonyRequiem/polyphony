using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

/// <summary>
/// Tests for <see cref="GhClient.MergePullRequestAsync"/> and
/// <see cref="GhClient.GetPullRequestStateAsync"/>. Covers argument
/// composition, success-path SHA reconciliation, idempotent
/// already-merged handling, and timeout reconciliation. Kept separate
/// from <see cref="GhClientRetryTests"/> to keep each file focused.
///
/// The fast policy (50 ms per attempt, 1 ms backoff) keeps the suite
/// well under a second even for tests that deliberately trigger
/// timeouts.
/// </summary>
public sealed class GhClientMergeTests
{
    private static readonly GhClientPolicy FastRetry = new(
        maxAttempts: 3,
        perAttemptTimeout: TimeSpan.FromMilliseconds(50),
        initialBackoff: TimeSpan.FromMilliseconds(1));

    private static readonly GhClientPolicy FastNoRetry = new(
        maxAttempts: 1,
        perAttemptTimeout: TimeSpan.FromMilliseconds(50),
        initialBackoff: TimeSpan.Zero);

    private const string ViewedJson =
        """{"number":42,"state":"MERGED","mergeCommit":{"oid":"deadbeef"},"headRefName":"impl/1-2","headRefOid":"abc"}""";

    private static void StubPrViewMerged(FakeProcessRunner fake)
        => fake.WhenStartsWith("gh", ["pr", "view"], new ProcessResult(0, ViewedJson, ""));

    // ─── Argument composition ─────────────────────────────────────────────

    [Fact]
    public async Task MergePullRequestAsync_DefaultFlags_EmitsBareMergeCommandWithoutAdminOrDelete()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastNoRetry);

        await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash);

        var merge = fake.Invocations.Single(i => i.Arguments[1] == "merge");
        merge.Arguments.ShouldBe(["pr", "merge", "42", "--repo", "o/r", "--squash"]);
    }

    [Fact]
    public async Task MergePullRequestAsync_MergeMethod_EmitsMergeFlag()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastNoRetry);

        await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Merge);

        var merge = fake.Invocations.Single(i => i.Arguments[1] == "merge");
        merge.Arguments.ShouldContain("--merge");
        merge.Arguments.ShouldNotContain("--squash");
    }

    [Fact]
    public async Task MergePullRequestAsync_RebaseMethod_EmitsRebaseFlag()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastNoRetry);

        await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Rebase);

        var merge = fake.Invocations.Single(i => i.Arguments[1] == "merge");
        merge.Arguments.ShouldContain("--rebase");
    }

    [Fact]
    public async Task MergePullRequestAsync_AdminTrue_EmitsAdminPresenceFlag()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastNoRetry);

        await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash, admin: true);

        var merge = fake.Invocations.Single(i => i.Arguments[1] == "merge");
        merge.Arguments.ShouldContain("--admin");
    }

    [Fact]
    public async Task MergePullRequestAsync_DeleteBranchTrue_EmitsDeleteBranchAsPresenceFlag()
    {
        // Critical: --delete-branch is a presence flag, NOT --delete-branch=true.
        // Tests against FakeProcessRunner enforce this since arg matching is exact.
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastNoRetry);

        await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash, deleteBranch: true);

        var merge = fake.Invocations.Single(i => i.Arguments[1] == "merge");
        merge.Arguments.ShouldContain("--delete-branch");
        merge.Arguments.ShouldNotContain("--delete-branch=true");
        merge.Arguments.ShouldNotContain("true");
    }

    [Fact]
    public async Task MergePullRequestAsync_DeleteBranchFalse_OmitsFlagEntirely()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastNoRetry);

        await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash, deleteBranch: false);

        var merge = fake.Invocations.Single(i => i.Arguments[1] == "merge");
        merge.Arguments.ShouldNotContain("--delete-branch");
    }

    [Fact]
    public async Task MergePullRequestAsync_MatchHeadCommitSet_EmitsFlagAndValueAsTwoArgs()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastNoRetry);

        await client.MergePullRequestAsync(
            "o/r", 42, GhMergeMethod.Squash, matchHeadCommit: "cafef00d");

        var merge = fake.Invocations.Single(i => i.Arguments[1] == "merge");
        var idx = merge.Arguments.ToList().IndexOf("--match-head-commit");
        idx.ShouldBeGreaterThan(0);
        merge.Arguments[idx + 1].ShouldBe("cafef00d");
    }

    [Fact]
    public async Task MergePullRequestAsync_MatchHeadCommitNullOrEmpty_OmitsFlag()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastNoRetry);

        await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash, matchHeadCommit: null);
        await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash, matchHeadCommit: "");

        foreach (var inv in fake.Invocations.Where(i => i.Arguments[1] == "merge"))
        {
            inv.Arguments.ShouldNotContain("--match-head-commit");
        }
    }

    // ─── Success path: SHA from follow-up pr view ─────────────────────────

    [Fact]
    public async Task MergePullRequestAsync_Success_PopulatesMergeShaFromPrView()
    {
        // gh pr merge stdout is unreliable for the SHA — verify we follow up
        // with pr view and extract from mergeCommit.oid.
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastNoRetry);

        var result = await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash);

        result.Succeeded.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeFalse();
        result.PrNumber.ShouldBe(42);
        result.MergeSha.ShouldBe("deadbeef");
    }

    [Fact]
    public async Task MergePullRequestAsync_Success_PrViewFails_StillReturnsSucceededWithNullSha()
    {
        // pr view is best-effort — if it errors out, we still report the
        // merge as successful but with no SHA.
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        fake.WhenStartsWith("gh", ["pr", "view"], new ProcessResult(1, "", "boom"));
        var client = new GhClient(fake, FastNoRetry);

        var result = await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash);

        result.Succeeded.ShouldBeTrue();
        result.MergeSha.ShouldBeNull();
    }

    // ─── Idempotent already-merged path ───────────────────────────────────

    [Fact]
    public async Task MergePullRequestAsync_AlreadyMergedStderr_ReconcilesAsAlreadyMergedSuccess()
    {
        // gh exit non-zero with "already merged" stderr; pr view confirms
        // MERGED state; result is success with AlreadyMerged=true.
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"],
            new ProcessResult(1, "", "the pull request is already merged"));
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastNoRetry);

        var result = await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash);

        result.Succeeded.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeSha.ShouldBe("deadbeef");
    }

    [Fact]
    public async Task MergePullRequestAsync_AlreadyMergedStderr_ButPrViewSaysOpen_StillThrows()
    {
        // Defensive: if stderr says "already merged" but pr view contradicts
        // (state = OPEN), we propagate the original error rather than lie.
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"],
            new ProcessResult(1, "", "the pull request is already merged"));
        fake.WhenStartsWith("gh", ["pr", "view"],
            new ProcessResult(0, """{"number":42,"state":"OPEN","mergeCommit":null,"headRefName":"impl/1-2","headRefOid":"abc"}""", ""));
        var client = new GhClient(fake, FastNoRetry);

        await Should.ThrowAsync<ExternalToolException>(() =>
            client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash));
    }

    [Fact]
    public async Task MergePullRequestAsync_NonZeroExit_NotAlreadyMerged_ThrowsExternalToolException()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "merge"],
            new ProcessResult(1, "", "review approval required"));
        var client = new GhClient(fake, FastNoRetry);

        var ex = await Should.ThrowAsync<ExternalToolException>(() =>
            client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash));
        ex.Stderr.ShouldContain("review approval required");
    }

    // ─── Timeout reconciliation ───────────────────────────────────────────

    [Fact]
    public async Task MergePullRequestAsync_TimeoutThenServerRecordsMerged_ReturnsAlreadyMergedSuccess()
    {
        // pr merge hangs (per-attempt timeout fires), but pr view shows the
        // PR as MERGED on the server. Treat as idempotent success.
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "merge" }, StringComparer.Ordinal),
            HangForeverAsync);
        StubPrViewMerged(fake);
        var client = new GhClient(fake, FastRetry);

        var result = await client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash);

        result.Succeeded.ShouldBeTrue();
        result.AlreadyMerged.ShouldBeTrue();
        result.MergeSha.ShouldBe("deadbeef");
        // Reconciliation should fire on the FIRST timeout — no retries needed.
        fake.Invocations.Count(i => i.Arguments[1] == "merge").ShouldBe(1);
    }

    [Fact]
    public async Task MergePullRequestAsync_TimeoutAndPrViewSaysOpen_RetriesUntilExhausted()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "merge" }, StringComparer.Ordinal),
            HangForeverAsync);
        fake.WhenStartsWith("gh", ["pr", "view"],
            new ProcessResult(0, """{"number":42,"state":"OPEN","mergeCommit":null}""", ""));
        var client = new GhClient(fake, FastRetry);

        var ex = await Should.ThrowAsync<ExternalToolTimeoutException>(() =>
            client.MergePullRequestAsync("o/r", 42, GhMergeMethod.Squash));

        ex.Attempts.ShouldBe(3);
        fake.Invocations.Count(i => i.Arguments[1] == "merge").ShouldBe(3);
    }

    // ─── GetPullRequestStateAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestStateAsync_HappyPath_ParsesAllFields()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "view"], new ProcessResult(0, ViewedJson, ""));
        var client = new GhClient(fake, FastNoRetry);

        var state = await client.GetPullRequestStateAsync("o/r", 42);

        state.ShouldNotBeNull();
        state.Number.ShouldBe(42);
        state.State.ShouldBe("MERGED");
        state.MergeCommitSha.ShouldBe("deadbeef");
        state.HeadRefName.ShouldBe("impl/1-2");
        state.HeadRefOid.ShouldBe("abc");
    }

    [Fact]
    public async Task GetPullRequestStateAsync_NullMergeCommit_ReturnsNullSha()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "view"],
            new ProcessResult(0, """{"number":42,"state":"OPEN","mergeCommit":null,"headRefName":"x","headRefOid":"y"}""", ""));
        var client = new GhClient(fake, FastNoRetry);

        var state = await client.GetPullRequestStateAsync("o/r", 42);

        state.ShouldNotBeNull();
        state.State.ShouldBe("OPEN");
        state.MergeCommitSha.ShouldBeNull();
    }

    [Fact]
    public async Task GetPullRequestStateAsync_NonZeroExit_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "view"], new ProcessResult(1, "", "not found"));
        var client = new GhClient(fake, FastNoRetry);

        var state = await client.GetPullRequestStateAsync("o/r", 42);

        state.ShouldBeNull();
    }

    [Fact]
    public async Task GetPullRequestStateAsync_RequestsExpectedJsonFields()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "view"], new ProcessResult(0, ViewedJson, ""));
        var client = new GhClient(fake, FastNoRetry);

        await client.GetPullRequestStateAsync("o/r", 42);

        var view = fake.Invocations.Single(i => i.Arguments[1] == "view");
        view.Arguments.ShouldBe(
            ["pr", "view", "42", "--repo", "o/r", "--json", "number,state,mergeCommit,headRefName,headRefOid"]);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static async Task<ProcessResult> HangForeverAsync(IReadOnlyList<string> _, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return default!; // unreachable
    }
}
