using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

/// <summary>
/// Unit tests for <see cref="GhClient.ClosePullRequestAsync"/>. The
/// cascade-remedy contract is identical to
/// <see cref="GhClient.EditPullRequestBodyAsync"/> — never throw on tool
/// failure, surface a routable bool. These tests pin both that contract
/// and the argv shape (with and without the optional --comment).
///
/// <para>Mirrors the FastRetry policy used by
/// <see cref="GhClientEditAndCommentTests"/> so the suite stays
/// sub-second even when timeouts fire.</para>
/// </summary>
public sealed class GhClientCloseTests
{
    private static readonly GhClientPolicy FastRetry = new(
        maxAttempts: 3,
        perAttemptTimeout: TimeSpan.FromMilliseconds(50),
        initialBackoff: TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task ClosePullRequest_WithComment_HappyPath_ReturnsTrue_ArgsIncludeComment()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "close" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        var client = new GhClient(fake, FastRetry);

        var ok = await client.ClosePullRequestAsync("o/r", 42, "🔄 Recreating after drift", default);

        ok.ShouldBeTrue();
        var inv = fake.Invocations.Single();
        inv.Arguments.ShouldBe(["pr", "close", "42", "--repo", "o/r", "--comment", "🔄 Recreating after drift"]);
    }

    [Fact]
    public async Task ClosePullRequest_EmptyComment_HappyPath_OmitsCommentFlag()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "close" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        var client = new GhClient(fake, FastRetry);

        var ok = await client.ClosePullRequestAsync("o/r", 42, "", default);

        ok.ShouldBeTrue();
        fake.Invocations.Single().Arguments.ShouldBe(["pr", "close", "42", "--repo", "o/r"]);
    }

    [Fact]
    public async Task ClosePullRequest_NullComment_HappyPath_OmitsCommentFlag()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "close" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(0, "", "")));
        var client = new GhClient(fake, FastRetry);

        var ok = await client.ClosePullRequestAsync("o/r", 42, null!, default);

        ok.ShouldBeTrue();
        fake.Invocations.Single().Arguments.ShouldBe(["pr", "close", "42", "--repo", "o/r"]);
    }

    [Fact]
    public async Task ClosePullRequest_NonZeroExit_ReturnsFalse_NoThrow_NoRetry()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "close" }, StringComparer.Ordinal),
            (_, _) => Task.FromResult(new ProcessResult(1, "", "could not close PR: branch protection")));
        var client = new GhClient(fake, FastRetry);

        var ok = await client.ClosePullRequestAsync("o/r", 42, "comment", default);

        ok.ShouldBeFalse();
        fake.Invocations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ClosePullRequest_AllAttemptsTimeout_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "close" }, StringComparer.Ordinal),
            HangForeverAsync);
        var client = new GhClient(fake, FastRetry);

        var ok = await client.ClosePullRequestAsync("o/r", 42, "comment", default);

        ok.ShouldBeFalse();
        fake.Invocations.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ClosePullRequest_TransientTimeoutThenSuccess_ReturnsTrue()
    {
        var fake = new FakeProcessRunner();
        int call = 0;
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "close" }, StringComparer.Ordinal),
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

        (await client.ClosePullRequestAsync("o/r", 42, "comment", default)).ShouldBeTrue();
        fake.Invocations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ClosePullRequest_RejectsInvalidArgs()
    {
        var fake = new FakeProcessRunner();
        var client = new GhClient(fake, FastRetry);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await client.ClosePullRequestAsync("", 1, "comment"));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await client.ClosePullRequestAsync("o/r", 0, "comment"));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await client.ClosePullRequestAsync("o/r", -7, "comment"));
    }

    [Fact]
    public async Task ClosePullRequest_CallerCancellation_PropagatesAsOperationCanceled()
    {
        var fake = new FakeProcessRunner();
        fake.WhenAsync(
            (e, a) => e == "gh" && a.Take(2).SequenceEqual(new[] { "pr", "close" }, StringComparer.Ordinal),
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return default!;
            });
        var client = new GhClient(fake, FastRetry);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await client.ClosePullRequestAsync("o/r", 1, "c", cts.Token));
        fake.Invocations.Count.ShouldBe(1);
    }

    private static async Task<ProcessResult> HangForeverAsync(IReadOnlyList<string> _, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return default!;
    }
}
