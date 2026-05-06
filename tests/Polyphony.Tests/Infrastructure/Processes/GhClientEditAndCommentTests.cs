using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

/// <summary>
/// Unit tests for <see cref="GhClient.EditPullRequestBodyAsync"/> and
/// <see cref="GhClient.CommentPullRequestAsync"/>. The cascade-remedy
/// contract is "always return a routable bool — never throw on tool
/// failure", so most of these tests pin that contract: timeout retries,
/// non-zero exits, and stdin payload preservation.
///
/// <para>Mirrors the FastRetry policy used by <see cref="GhClientRetryTests"/>
/// so the suite stays sub-second even when timeouts fire.</para>
/// </summary>
public sealed class GhClientEditAndCommentTests
{
    private static readonly GhClientPolicy FastRetry = new(
        maxAttempts: 3,
        perAttemptTimeout: TimeSpan.FromMilliseconds(50),
        initialBackoff: TimeSpan.FromMilliseconds(1));

    // ─── EditPullRequestBodyAsync ─────────────────────────────────────────

    [Fact]
    public async Task EditPullRequestBody_HappyPath_ReturnsTrue_StdinCarriesBody()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "edit" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        var client = new GhClient(fake, FastRetry);

        var ok = await client.EditPullRequestBodyAsync("o/r", 42, "new body", default);

        ok.ShouldBeTrue();
        var inv = fake.Invocations.Single();
        inv.Arguments.ShouldBe(["pr", "edit", "42", "--repo", "o/r", "--body-file", "-"]);
        inv.Stdin.ShouldBe("new body");
    }

    [Fact]
    public async Task EditPullRequestBody_NonZeroExit_ReturnsFalse_NoThrow()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "edit" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(1, "", "permission denied")));
        var client = new GhClient(fake, FastRetry);

        var ok = await client.EditPullRequestBodyAsync("o/r", 42, "body", default);

        ok.ShouldBeFalse();
        // No retry on non-zero exit.
        fake.Invocations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EditPullRequestBody_TransientTimeoutThenSuccess_ReturnsTrue()
    {
        var fake = new FakeProcessRunner();
        int call = 0;
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "edit" }, StringComparer.Ordinal),
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return default!;
                }
                return new ProcessResult(0, "", "");
            });
        var client = new GhClient(fake, FastRetry);

        var ok = await client.EditPullRequestBodyAsync("o/r", 42, "body", default);

        ok.ShouldBeTrue();
        fake.Invocations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EditPullRequestBody_AllAttemptsTimeout_ReturnsFalse_NoThrow()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "edit" }, StringComparer.Ordinal),
            HangForeverAsync);
        var client = new GhClient(fake, FastRetry);

        var ok = await client.EditPullRequestBodyAsync("o/r", 42, "body", default);

        ok.ShouldBeFalse();
        fake.Invocations.Count.ShouldBe(3);
    }

    [Fact]
    public async Task EditPullRequestBody_BodyWithNewlines_PreservedOnStdin()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "edit" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        var client = new GhClient(fake, FastRetry);

        var body = "line one\r\nline two\nline three\n";
        var ok = await client.EditPullRequestBodyAsync("o/r", 1, body, default);

        ok.ShouldBeTrue();
        fake.Invocations.Single().Stdin.ShouldBe(body);
    }

    [Fact]
    public async Task EditPullRequestBody_BodyWithUnicode_PreservedOnStdin()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "edit" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        var client = new GhClient(fake, FastRetry);

        var body = "🔄 Auto-rebased — résumé naïveté 日本語";
        var ok = await client.EditPullRequestBodyAsync("o/r", 1, body, default);

        ok.ShouldBeTrue();
        fake.Invocations.Single().Stdin.ShouldBe(body);
    }

    [Fact]
    public async Task EditPullRequestBody_RejectsInvalidArgs()
    {
        var fake = new FakeProcessRunner();
        var client = new GhClient(fake, FastRetry);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await client.EditPullRequestBodyAsync("", 1, "body"));
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await client.EditPullRequestBodyAsync("o/r", 1, null!));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await client.EditPullRequestBodyAsync("o/r", 0, "body"));
    }

    [Fact]
    public async Task EditPullRequestBody_CallerCancellation_PropagatesAsOperationCanceled()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "edit" }, StringComparer.Ordinal),
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return default!;
            });
        var client = new GhClient(fake, FastRetry);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await client.EditPullRequestBodyAsync("o/r", 1, "body", cts.Token));
        // No retry — caller cancellation aborts immediately.
        fake.Invocations.Count.ShouldBe(1);
    }

    // ─── CommentPullRequestAsync ──────────────────────────────────────────

    [Fact]
    public async Task CommentPullRequest_HappyPath_ReturnsTrue_StdinCarriesBody()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "comment" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "https://github.com/o/r/pull/42#issuecomment-123\n", "")));
        var client = new GhClient(fake, FastRetry);

        var ok = await client.CommentPullRequestAsync("o/r", 42, "🔄 Auto-rebased onto plan/100", default);

        ok.ShouldBeTrue();
        var inv = fake.Invocations.Single();
        inv.Arguments.ShouldBe(["pr", "comment", "42", "--repo", "o/r", "--body-file", "-"]);
        inv.Stdin.ShouldBe("🔄 Auto-rebased onto plan/100");
    }

    [Fact]
    public async Task CommentPullRequest_NonZeroExit_ReturnsFalse_NoThrow()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "comment" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(1, "", "comment failed")));
        var client = new GhClient(fake, FastRetry);

        var ok = await client.CommentPullRequestAsync("o/r", 42, "body", default);

        ok.ShouldBeFalse();
        fake.Invocations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CommentPullRequest_AllAttemptsTimeout_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "comment" }, StringComparer.Ordinal),
            HangForeverAsync);
        var client = new GhClient(fake, FastRetry);

        var ok = await client.CommentPullRequestAsync("o/r", 42, "body", default);

        ok.ShouldBeFalse();
        fake.Invocations.Count.ShouldBe(3);
    }

    [Fact]
    public async Task CommentPullRequest_TransientTimeoutThenSuccess_ReturnsTrue()
    {
        var fake = new FakeProcessRunner();
        int call = 0;
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "comment" }, StringComparer.Ordinal),
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return default!;
                }
                return new ProcessResult(0, "", "");
            });
        var client = new GhClient(fake, FastRetry);

        (await client.CommentPullRequestAsync("o/r", 42, "body", default)).ShouldBeTrue();
        fake.Invocations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CommentPullRequest_RejectsInvalidArgs()
    {
        var fake = new FakeProcessRunner();
        var client = new GhClient(fake, FastRetry);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await client.CommentPullRequestAsync("", 1, "body"));
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await client.CommentPullRequestAsync("o/r", 1, null!));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await client.CommentPullRequestAsync("o/r", -5, "body"));
    }

    private static async Task<ProcessResult> HangForeverAsync(IReadOnlyList<string> _, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return default!;
    }
}
