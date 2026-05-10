using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

/// <summary>
/// Tests for <see cref="GhClient"/> timeout + retry behavior. These are
/// kept separate from the happy-path <see cref="GhClientTests"/> so each
/// file stays focused on one axis.
///
/// The fast policy used throughout (50ms per attempt, 1ms backoff) keeps
/// the suite well under a second even when several tests deliberately
/// trigger timeouts.
/// </summary>
public sealed class GhClientRetryTests
{
    private static readonly GhClientPolicy FastRetry = new(
        maxAttempts: 3,
        perAttemptTimeout: TimeSpan.FromMilliseconds(50),
        initialBackoff: TimeSpan.FromMilliseconds(1));

    private static readonly GhClientPolicy FastNoRetry = new(
        maxAttempts: 1,
        perAttemptTimeout: TimeSpan.FromMilliseconds(50),
        initialBackoff: TimeSpan.Zero);

    // ─── GhClientPolicy validation ────────────────────────────────────────

    [Fact]
    public void Policy_RejectsZeroOrNegativeMaxAttempts()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new GhClientPolicy(0, TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new GhClientPolicy(-1, TimeSpan.FromSeconds(1), TimeSpan.Zero));
    }

    [Fact]
    public void Policy_RejectsZeroOrNegativeTimeout()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new GhClientPolicy(1, TimeSpan.Zero, TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new GhClientPolicy(1, TimeSpan.FromSeconds(-1), TimeSpan.Zero));
    }

    [Fact]
    public void Policy_RejectsNegativeBackoff()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new GhClientPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Policy_DefaultsAreSensible()
    {
        GhClientPolicy.Default.MaxAttempts.ShouldBe(3);
        GhClientPolicy.Default.PerAttemptTimeout.ShouldBe(TimeSpan.FromSeconds(60));
        GhClientPolicy.Default.InitialBackoff.ShouldBe(TimeSpan.FromSeconds(1));
        GhClientPolicy.NoRetry.MaxAttempts.ShouldBe(1);
    }

    // ─── GetAuthStatusAsync timeout ───────────────────────────────────────

    [Fact]
    public async Task GetAuthStatusAsync_AllAttemptsTimeout_ReturnsUnauthenticatedWithDetail()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.SequenceEqual(new[] { "auth", "status" }, StringComparer.Ordinal),
            HangForeverAsync);
        var client = new GhClient(fake, FastRetry);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeFalse();
        status.Detail.ShouldContain("timed out");
        // 3 attempts because policy is FastRetry.
        fake.Invocations.Count.ShouldBe(3);
    }

    [Fact]
    public async Task GetAuthStatusAsync_TransientTimeoutThenSuccess_Succeeds()
    {
        var fake = new FakeProcessRunner();
        int call = 0;
        fake.WhenAsync(
            (e, a) => e == "gh" && a.SequenceEqual(new[] { "auth", "status" }, StringComparer.Ordinal),
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return default!;
                }
                return new ProcessResult(0, "", "✓ Logged in");
            });
        var client = new GhClient(fake, FastRetry);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeTrue();
        fake.Invocations.Count.ShouldBe(2);
    }

    // ─── ListPullRequestsAsync timeout ────────────────────────────────────

    [Fact]
    public async Task ListPullRequestsAsync_AllAttemptsTimeout_ThrowsExternalToolTimeout()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            HangForeverAsync);
        var client = new GhClient(fake, FastRetry);

        var ex = await Should.ThrowAsync<ExternalToolTimeoutException>(async () =>
            await client.ListPullRequestsAsync("o/r", new PrListFilters()));

        ex.Executable.ShouldBe("gh");
        ex.Attempts.ShouldBe(3);
        ex.TimeoutPerAttempt.ShouldBe(TimeSpan.FromMilliseconds(50));
        fake.Invocations.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ListPullRequestsAsync_TransientTimeoutThenSuccess_ReturnsParsedList()
    {
        var fake = new FakeProcessRunner();
        int call = 0;
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return default!;
                }
                return new ProcessResult(0, """[{"number":7,"headRefName":"feature/x","url":"https://x","mergedAt":null}]""", "");
            });
        var client = new GhClient(fake, FastRetry);

        var prs = await client.ListPullRequestsAsync("o/r", new PrListFilters());

        prs.Count.ShouldBe(1);
        prs[0].Number.ShouldBe(7);
        fake.Invocations.Count.ShouldBe(2);
    }

    // ─── CreatePullRequestAsync — timeout + reconciliation ────────────────

    [Fact]
    public async Task CreatePullRequestAsync_AllAttemptsTimeout_NoExistingPr_ThrowsTimeout()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "create"], default!);
        // Override the create matcher with an async hang.
        var fake2 = new FakeProcessRunner();
        fake2.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "create" }, StringComparer.Ordinal),
            HangForeverAsync);
        // Reconciliation list call returns empty (no PR exists).
        fake2.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "[]", "")));
        var client = new GhClient(fake2, FastRetry);

        var ex = await Should.ThrowAsync<ExternalToolTimeoutException>(async () =>
            await client.CreatePullRequestAsync("o/r", "main", "feature/x", "t", "b"));

        ex.Attempts.ShouldBe(3);
        // 3 create attempts + 3 reconciliation list calls (one per attempt's timeout) = 6 invocations.
        fake2.Invocations.Count(i => i.Arguments[1] == "create").ShouldBe(3);
        fake2.Invocations.Count(i => i.Arguments[1] == "list").ShouldBe(3);
    }

    [Fact]
    public async Task CreatePullRequestAsync_TimeoutThenServerSidePrFound_ReturnsReconciledUrl()
    {
        var fake = new FakeProcessRunner();
        // Create call hangs forever — the timed-out attempt "succeeded" server-side.
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "create" }, StringComparer.Ordinal),
            HangForeverAsync);
        // Reconciliation list finds the PR.
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0,
                """[{"number":99,"headRefName":"feature/x","url":"https://github.com/o/r/pull/99","mergedAt":null}]""",
                "")));
        var client = new GhClient(fake, FastRetry);

        var url = await client.CreatePullRequestAsync("o/r", "main", "feature/x", "t", "b");

        url.ShouldBe("https://github.com/o/r/pull/99");
        // After attempt 1 timeout, reconciliation finds the PR — no attempt 2.
        fake.Invocations.Count(i => i.Arguments[1] == "create").ShouldBe(1);
        fake.Invocations.Count(i => i.Arguments[1] == "list").ShouldBe(1);
    }

    [Fact]
    public async Task CreatePullRequestAsync_AlreadyExistsStderr_ReconcilesToExistingUrl()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "create" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(1, "",
                "a pull request for branch feature/x into branch main already exists: https://github.com/o/r/pull/42")));
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0,
                """[{"number":42,"headRefName":"feature/x","url":"https://github.com/o/r/pull/42","mergedAt":null}]""",
                "")));
        var client = new GhClient(fake, FastRetry);

        var url = await client.CreatePullRequestAsync("o/r", "main", "feature/x", "t", "b");

        url.ShouldBe("https://github.com/o/r/pull/42");
        // Single create attempt + single reconciliation list (no retry on non-zero exit).
        fake.Invocations.Count(i => i.Arguments[1] == "create").ShouldBe(1);
        fake.Invocations.Count(i => i.Arguments[1] == "list").ShouldBe(1);
    }

    [Fact]
    public async Task CreatePullRequestAsync_AlreadyExistsButReconcileEmpty_ThrowsExternalToolException()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "create" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(1, "", "already exists somewhere")));
        // Reconciliation returns empty (race: PR was deleted before list).
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "[]", "")));
        var client = new GhClient(fake, FastRetry);

        await Should.ThrowAsync<ExternalToolException>(async () =>
            await client.CreatePullRequestAsync("o/r", "main", "feature/x", "t", "b"));
    }

    [Fact]
    public async Task CreatePullRequestAsync_NonZeroExit_NotAlreadyExists_ThrowsImmediately_NoRetry()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "create" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(1, "", "head branch does not exist")));
        var client = new GhClient(fake, FastRetry);

        var ex = await Should.ThrowAsync<ExternalToolException>(async () =>
            await client.CreatePullRequestAsync("o/r", "main", "feature/x", "t", "b"));

        ex.Stderr.ShouldContain("head branch does not exist");
        // Only one attempt — non-zero exits without "already exists" do not retry.
        fake.Invocations.Count(i => i.Arguments[1] == "create").ShouldBe(1);
    }

    // ─── Caller-driven cancellation ───────────────────────────────────────

    [Fact]
    public async Task CallerCancellation_PropagatesImmediately_NoRetry()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.SequenceEqual(new[] { "auth", "status" }, StringComparer.Ordinal),
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return default!;
            });
        var client = new GhClient(fake, FastRetry);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        // GetAuthStatusAsync swallows ExternalToolTimeoutException, but
        // a caller-driven cancellation must propagate as OperationCanceledException.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await client.GetAuthStatusAsync(cts.Token));

        // Only one invocation — the runner saw cancellation, not a timeout retry.
        fake.Invocations.Count.ShouldBe(1);
    }

    // ─── Single-attempt (NoRetry) policy ──────────────────────────────────

    [Fact]
    public async Task NoRetryPolicy_TimeoutFiresOnce_ThenThrows()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            HangForeverAsync);
        var client = new GhClient(fake, FastNoRetry);

        var ex = await Should.ThrowAsync<ExternalToolTimeoutException>(async () =>
            await client.ListPullRequestsAsync("o/r", new PrListFilters()));

        ex.Attempts.ShouldBe(1);
        fake.Invocations.Count.ShouldBe(1);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static async Task<ProcessResult> HangForeverAsync(IReadOnlyList<string> _, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return default!; // unreachable
    }

    // ─── Buffered output preserved through ExternalToolTimeoutException ───

    [Fact]
    public async Task ListPullRequestsAsync_AllAttemptsTimeout_PreservesLastBufferedStderr()
    {
        // FakeProcessRunner throws ProcessCanceledException directly to
        // simulate what real ProcessRunner does when killed mid-stderr.
        // GhClient must catch the typed exception ahead of bare OCE and
        // surface the buffered output through ExternalToolTimeoutException.
        var fake = new FakeProcessRunner();
        const string fakeStderr =
            "GH_DEBUG: GET https://api.github.com/repos/o/r/pulls\n" +
            "GH_DEBUG: waiting for response (300s)\n";
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            (args, _) => throw new ProcessCanceledException(
                "gh", args.ToArray(),
                bufferedStdout: "",
                bufferedStderr: fakeStderr,
                elapsed: TimeSpan.FromMilliseconds(50)));

        var client = new GhClient(fake, FastRetry);

        var ex = await Should.ThrowAsync<ExternalToolTimeoutException>(async () =>
            await client.ListPullRequestsAsync("o/r", new PrListFilters()));

        ex.Attempts.ShouldBe(3);
        ex.LastBufferedStderr.ShouldContain("GH_DEBUG: GET https://api.github.com");
        ex.LastBufferedStderr.ShouldContain("waiting for response");
        ex.LastElapsed.ShouldBe(TimeSpan.FromMilliseconds(50));
        ex.Message.ShouldContain("Last attempt stderr (tail)");
    }

    [Fact]
    public async Task ExternalToolTimeoutException_NoBufferedOutput_MessageHintsAtGhDebugFlag()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            HangForeverAsync);
        var client = new GhClient(fake, FastRetry);

        var ex = await Should.ThrowAsync<ExternalToolTimeoutException>(async () =>
            await client.ListPullRequestsAsync("o/r", new PrListFilters()));

        ex.LastBufferedStderr.ShouldBeEmpty();
        ex.Message.ShouldContain("no stderr emitted before kill");
        ex.Message.ShouldContain("GH_DEBUG=api");
    }

    [Fact]
    public async Task ExternalToolTimeoutException_LongStderr_TailTruncatedInMessage()
    {
        var fake = new FakeProcessRunner();
        // 8K of distinguishable head + a trailing marker we expect to see.
        var longStderr = new string('H', 8192) + "TAIL-MARKER-XYZ";
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            (args, _) => throw new ProcessCanceledException(
                "gh", args.ToArray(), "", longStderr, TimeSpan.FromMilliseconds(50)));
        var client = new GhClient(fake, FastRetry);

        var ex = await Should.ThrowAsync<ExternalToolTimeoutException>(async () =>
            await client.ListPullRequestsAsync("o/r", new PrListFilters()));

        // Full buffer survives on the property.
        ex.LastBufferedStderr.Length.ShouldBe(longStderr.Length);
        // Message keeps the TAIL (most diagnostically useful), not the head.
        ex.Message.ShouldContain("TAIL-MARKER-XYZ");
        ex.Message.ShouldStartWith("gh pr list");
    }

    [Fact]
    public async Task ExternalToolTimeoutException_TokenInStderr_RedactedInMessage()
    {
        var fake = new FakeProcessRunner();
        const string stderrWithToken =
            "GH_DEBUG: > Authorization: Bearer gho_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789\n" +
            "GH_DEBUG: bare ghp_ZyXwVuTsRqPoNmLkJiHgFeDcBa9876543210TOKEN here\n";
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "list" }, StringComparer.Ordinal),
            (args, _) => throw new ProcessCanceledException(
                "gh", args.ToArray(), "", stderrWithToken, TimeSpan.FromMilliseconds(50)));
        var client = new GhClient(fake, FastRetry);

        var ex = await Should.ThrowAsync<ExternalToolTimeoutException>(async () =>
            await client.ListPullRequestsAsync("o/r", new PrListFilters()));

        // Raw buffer keeps the original (callers may need it for non-display use).
        ex.LastBufferedStderr.ShouldContain("gho_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789");
        // But the user-facing Message must be redacted.
        ex.Message.ShouldNotContain("gho_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789");
        ex.Message.ShouldNotContain("ghp_ZyXwVuTsRqPoNmLkJiHgFeDcBa9876543210TOKEN");
        ex.Message.ShouldContain("[REDACTED");
    }

    // ─── GH_PROMPT_DISABLED env propagated to subprocess ──────────────────

    [Fact]
    public async Task RunAsync_AppliesGhPromptDisabledAndNoColorEnv()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "auth", "status" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "", "✓ Logged in")));
        var client = new GhClient(fake, FastNoRetry);

        await client.GetAuthStatusAsync();

        fake.Invocations.Count.ShouldBe(1);
        var inv = fake.Invocations[0];
        inv.Environment.ShouldNotBeNull();
        inv.Environment!["GH_PROMPT_DISABLED"].ShouldBe("1");
        inv.Environment!["NO_COLOR"].ShouldBe("1");
    }

    /// <summary>
    /// Issue #209 regression: every gh invocation must request stdin
    /// closure so the child sees EOF on read instead of inheriting a
    /// stale console handle from the conductor → polyphony chain on
    /// Windows. <see cref="GhClient.RunSingleAttemptAsync"/> is the
    /// single funnel for all gh subprocess spawns; asserting closeStdin
    /// on it covers every gh verb in the codebase.
    /// </summary>
    [Fact]
    public async Task RunAsync_AlwaysRequestsCloseStdin()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "auth", "status" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "", "✓ Logged in")));
        var client = new GhClient(fake, FastNoRetry);

        await client.GetAuthStatusAsync();

        fake.Invocations.Count.ShouldBe(1);
        fake.Invocations[0].CloseStdin.ShouldBeTrue();
    }
}
