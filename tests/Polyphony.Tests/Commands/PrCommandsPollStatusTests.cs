using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony pr poll-status</c>. Stubs the
/// underlying <c>gh pr view</c> call via <see cref="FakeProcessRunner"/>
/// and asserts on the platform-neutral JSON the verb emits. Covers the
/// 5 normalized states, reviewer vote normalization, mergeable mapping,
/// URL parsing, error envelopes, and the optional front-matter parse.
/// </summary>
public sealed class PrCommandsPollStatusTests : CommandTestBase
{
    private (PrCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new PrCommands(git, gh, twig, Repository, Config, new Polyphony.Locking.RunLockStore(), new Polyphony.Locking.RunLockPathResolver(git), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git)), runner);
    }

    private static void StubGhPrView(FakeProcessRunner runner, string repoSlug, int prNumber, string json)
    {
        runner.WhenStartsWith(
            "gh",
            new[] { "pr", "view", prNumber.ToString(), "--repo", repoSlug, "--json" },
            new ProcessResult(0, json, ""));
        // Option B: poll-status now also calls `gh api graphql` to fetch
        // review threads. Tests that don't care about threads get an empty
        // page by default; tests that do care can override by registering
        // a more specific responder before the call.
        StubGhReviewThreads(runner, EmptyThreadsResponse);
    }

    private static void StubGhPrViewNotFound(FakeProcessRunner runner, string repoSlug, int prNumber)
    {
        runner.WhenStartsWith(
            "gh",
            new[] { "pr", "view", prNumber.ToString(), "--repo", repoSlug, "--json" },
            new ProcessResult(1, "", "no pull requests found"));
        // The poll-data error short-circuits before the graphql call, but
        // register a defensive stub anyway in case future verb logic changes.
        StubGhReviewThreads(runner, EmptyThreadsResponse);
    }

    private const string EmptyThreadsResponse =
        """{"data":{"repository":{"pullRequest":{"reviewThreads":{"pageInfo":{"hasNextPage":false},"nodes":[]}}}}}""";

    private static void StubGhReviewThreads(FakeProcessRunner runner, string graphqlResponse)
        => runner.WhenStartsWith(
            "gh",
            new[] { "api", "graphql" },
            new ProcessResult(0, graphqlResponse, ""));

    private static string ThreadsResponse(bool hasNextPage, params (string Id, bool IsResolved, bool IsOutdated, string Author, string CreatedAt, int CommentCount)[] threads)
    {
        var nodes = string.Join(",", threads.Select(t =>
            "{\"id\":\"" + t.Id + "\"," +
            "\"isResolved\":" + (t.IsResolved ? "true" : "false") + "," +
            "\"isOutdated\":" + (t.IsOutdated ? "true" : "false") + "," +
            "\"comments\":{" +
                "\"totalCount\":" + t.CommentCount + "," +
                "\"nodes\":[{\"author\":{\"login\":\"" + t.Author + "\"},\"createdAt\":\"" + t.CreatedAt + "\"}]" +
            "}}"));
        return "{\"data\":{\"repository\":{\"pullRequest\":{\"reviewThreads\":{" +
            "\"pageInfo\":{\"hasNextPage\":" + (hasNextPage ? "true" : "false") + "}," +
            "\"nodes\":[" + nodes + "]" +
            "}}}}}";
    }

    private static PrPollStatusResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrPollStatusResult)!;

    private static string PrJson(
        int number,
        string state,
        string reviewDecision,
        string mergeable = "MERGEABLE",
        string headRefName = "feature/x",
        string headRefOid = "abc123",
        string baseRefName = "main",
        string body = "",
        string mergedAt = "",
        string mergeOid = "",
        string reviewsJson = "[]",
        string authorLogin = "",
        string commentsJson = "[]")
    {
        var mergeCommitClause = string.IsNullOrEmpty(mergeOid)
            ? "null"
            : $$"""{"oid":"{{mergeOid}}"}""";
        var mergedAtClause = string.IsNullOrEmpty(mergedAt) ? "null" : $"\"{mergedAt}\"";
        var encodedBody = JsonSerializer.Serialize(body);
        var authorClause = string.IsNullOrEmpty(authorLogin)
            ? "null"
            : $$"""{"login":"{{authorLogin}}"}""";
        return $$"""
            {
              "number": {{number}},
              "state": "{{state}}",
              "reviewDecision": "{{reviewDecision}}",
              "mergeable": "{{mergeable}}",
              "headRefName": "{{headRefName}}",
              "headRefOid": "{{headRefOid}}",
              "baseRefName": "{{baseRefName}}",
              "mergedAt": {{mergedAtClause}},
              "mergeCommit": {{mergeCommitClause}},
              "body": {{encodedBody}},
              "reviews": {{reviewsJson}},
              "author": {{authorClause}},
              "comments": {{commentsJson}}
            }
            """;
    }

    private static string Comment(string author, string body, string createdAt)
    {
        var encodedBody = JsonSerializer.Serialize(body);
        return $$"""{"author":{"login":"{{author}}"},"body":{{encodedBody}},"createdAt":"{{createdAt}}"}""";
    }

    [Fact]
    public async Task PollStatus_InvalidUrl_ReturnsErrorEnvelope()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "not-a-url"));
        // Verb always exits 0 — routing-style; consumer reads state/error.
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("error");
        result.Error.ShouldNotBeNullOrEmpty();
        result.Policy.MergeAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task PollStatus_AdoUrlNotSupported_ReturnsErrorEnvelope()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://dev.azure.com/org/proj/_git/repo/pullrequest/123"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("error");
    }

    [Fact]
    public async Task PollStatus_PrNotFound_ReturnsErrorEnvelope()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrViewNotFound(runner, "owner/repo", 99);
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/99"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.State.ShouldBe("error");
        result.PrNumber.ShouldBe(99);
        result.RepoSlug.ShouldBe("owner/repo");
    }

    [Fact]
    public async Task PollStatus_OpenApproved_NormalizesToApproved()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Policy.MergeAllowed.ShouldBeTrue();
        result.Policy.BlockingReasons.ShouldBeEmpty();
    }

    [Fact]
    public async Task PollStatus_OpenChangesRequested_NormalizesToChangesRequested()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "CHANGES_REQUESTED"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("changes_requested");
        result.Policy.MergeAllowed.ShouldBeFalse();
        result.Policy.BlockingReasons.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task PollStatus_OpenNoDecision_NormalizesToPending()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("pending");
        result.Policy.MergeAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task PollStatus_Merged_NormalizesToMerged()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "MERGED", "APPROVED",
                mergedAt: "2026-05-06T10:00:00Z",
                mergeOid: "deadbeef"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("merged");
        result.MergeCommitSha.ShouldBe("deadbeef");
        result.MergedAt.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task PollStatus_Closed_NormalizesToClosed()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "CLOSED", ""));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("closed");
        result.Policy.MergeAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task PollStatus_MergeableConflicting_BlocksMergeAllowed()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED", mergeable: "CONFLICTING"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Mergeable.ShouldBe(false);
        result.Policy.MergeAllowed.ShouldBeFalse();
        result.Policy.BlockingReasons.ShouldContain(x => x.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PollStatus_MergeableUnknown_BlocksWhenApproved()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED", mergeable: "UNKNOWN"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.Mergeable.ShouldBeNull();
        result.Policy.MergeAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task PollStatus_ReviewersNormalized_AllVoteTypes()
    {
        var (cmd, runner) = CreateCommand();
        var reviews = """
            [
              {"author": {"login": "alice"}, "state": "APPROVED",          "submittedAt": "2026-05-06T10:00:00Z"},
              {"author": {"login": "bob"},   "state": "CHANGES_REQUESTED", "submittedAt": "2026-05-06T10:01:00Z"},
              {"author": {"login": "carol"}, "state": "COMMENTED",         "submittedAt": "2026-05-06T10:02:00Z"},
              {"author": {"login": "dave"},  "state": "DISMISSED",         "submittedAt": "2026-05-06T10:03:00Z"},
              {"author": {"login": "eve"},   "state": "PENDING",           "submittedAt": null}
            ]
            """;
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED", reviewsJson: reviews));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.Reviewers.Count.ShouldBe(5);
        result.Reviewers[0].Identity.ShouldBe("alice");
        result.Reviewers[0].Vote.ShouldBe("approved");
        result.Reviewers[1].Vote.ShouldBe("changes_requested");
        result.Reviewers[2].Vote.ShouldBe("commented");
        result.Reviewers[3].Vote.ShouldBe("dismissed");
        result.Reviewers[4].Vote.ShouldBe("pending");
    }

    [Fact]
    public async Task PollStatus_WithoutMetadataFlag_LeavesMetadataNull()
    {
        var (cmd, runner) = CreateCommand();
        var bodyWithFrontMatter = "---\nrequests_parent_change: true\n---\n## Plan body";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED", body: bodyWithFrontMatter));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.Metadata.ShouldBeNull();
    }

    [Fact]
    public async Task PollStatus_WithMetadataFlag_ParsesFrontMatter()
    {
        var (cmd, runner) = CreateCommand();
        var bodyWithFrontMatter =
            "---\nrequests_parent_change: true\nancestor_plan_generations:\n  root: 2\n  \"5678\": 1\n---\n## Plan body";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED", body: bodyWithFrontMatter));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42", includeMetadata: true));
        var result = Parse(output);
        result.Metadata.ShouldNotBeNull();
        result.Metadata!.RequestsParentChange.ShouldBeTrue();
        result.Metadata.AncestorPlanGenerations.Count.ShouldBe(2);
        result.Metadata.AncestorPlanGenerations["root"].ShouldBe(2);
        result.Metadata.AncestorPlanGenerations["5678"].ShouldBe(1);
    }

    [Fact]
    public async Task PollStatus_WithMetadataFlag_NoFrontMatter_ReturnsDefaults()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED", body: "Just a regular PR body."));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42", includeMetadata: true));
        var result = Parse(output);
        result.Metadata.ShouldNotBeNull();
        result.Metadata!.RequestsParentChange.ShouldBeFalse();
        result.Metadata.AncestorPlanGenerations.ShouldBeEmpty();
    }

    [Fact]
    public async Task PollStatus_JsonContract_IsSnakeCase()
    {
        // Pin the wire shape — workflow consumers parse the keys directly.
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED",
                mergedAt: "", body: "", mergeOid: ""));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        // Required snake-case keys we depend on in workflow YAML.
        output.ShouldContain("\"pr_url\"");
        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"repo_slug\"");
        output.ShouldContain("\"head_sha\"");
        output.ShouldContain("\"head_ref\"");
        output.ShouldContain("\"base_ref\"");
        output.ShouldContain("\"reviewers\"");
        output.ShouldContain("\"policy\"");
        output.ShouldContain("\"merge_allowed\"");
        output.ShouldContain("\"blocking_reasons\"");
    }

    [Fact]
    public async Task PollStatus_EchoesUrlAndSlug_ForTraceability()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.PrUrl.ShouldBe("https://github.com/owner/repo/pull/42");
        result.PrNumber.ShouldBe(42);
        result.RepoSlug.ShouldBe("owner/repo");
        result.HeadSha.ShouldBe("abc123");
        result.HeadRef.ShouldBe("feature/x");
        result.BaseRef.ShouldBe("main");
    }

    [Fact]
    public async Task PollStatus_TrailingSlashOrQuery_StillParsed()
    {
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42/files"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.PrNumber.ShouldBe(42);
    }

    // ----------------------------------------------------------------------
    // Magic-comment approval (issue #207)
    //
    // Recognized syntax: PR-author top-level comments matching
    //   ^\s*polyphony:approve\b
    //   ^\s*polyphony:request-changes\b
    // (case-insensitive, anchored to start of line).
    //
    // Per-author overlay: when a magic comment exists from the PR author AND
    // it's more recent than any native APPROVED|CHANGES_REQUESTED review by
    // the author, recompute state via the per-identity most-recent-wins
    // overlay. Cross-reviewer aggregation: changes_requested wins, then
    // approved, then pending.
    //
    // Non-author comments are ignored — they should use native reviews.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task PollStatus_MagicComment_AuthorApprove_OverridesPending()
    {
        // Single-user mode: author opens PR (can't self-review on GH),
        // posts polyphony:approve as a comment. Effective state = approved.
        var (cmd, runner) = CreateCommand();
        var comments = $"[{Comment("dangreen", "polyphony:approve LGTM ship it", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Policy.MergeAllowed.ShouldBeTrue();
        result.Reviewers.ShouldContain(r =>
            r.Identity == "dangreen" && r.Vote == "approved" && r.Source == "magic_comment");
    }

    [Fact]
    public async Task PollStatus_MagicComment_RequestChanges_NoLongerRecognized()
    {
        // Option B: `polyphony:request-changes` magic comment was the loop bug
        // (every poll re-read it). It is no longer recognized — reviewers must
        // use native review threads or platform CHANGES_REQUESTED reviews.
        // The author's comment here is treated as a normal comment with no
        // effect on derived state.
        var (cmd, runner) = CreateCommand();
        var comments = $"[{Comment("dangreen", "polyphony:request-changes plan needs work", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("pending");
        result.Reviewers.ShouldNotContain(r => r.Source == "magic_comment");
    }

    [Fact]
    public async Task PollStatus_MagicComment_NonAuthor_Ignored()
    {
        // A polyphony:approve from someone other than the PR author must be
        // ignored — they should use native review. Single-user mode rule.
        var (cmd, runner) = CreateCommand();
        var comments = $"[{Comment("alice", "polyphony:approve", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("pending");
        result.Reviewers.ShouldNotContain(r => r.Source == "magic_comment");
    }

    [Fact]
    public async Task PollStatus_MagicComment_MidLineMatch_NotRecognized()
    {
        // "see polyphony:approve below" must NOT match — anchored to line start.
        var (cmd, runner) = CreateCommand();
        var comments = $"[{Comment("dangreen", "see polyphony:approve below", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("pending");
        result.Reviewers.ShouldBeEmpty();
    }

    [Fact]
    public async Task PollStatus_MagicComment_OnNewLine_Recognized()
    {
        // Multi-line comments where one line matches at the start are valid.
        var (cmd, runner) = CreateCommand();
        var body = "Some narrative discussion.\npolyphony:approve\nThanks!";
        var comments = $"[{Comment("dangreen", body, "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
    }

    [Fact]
    public async Task PollStatus_MagicComment_MostRecentWins_AcrossMultipleAuthorComments()
    {
        // Author posted request-changes earlier, then approve later. Approve wins.
        var (cmd, runner) = CreateCommand();
        var comments = $"""
            [
              {Comment("dangreen", "polyphony:request-changes initial concern", "2026-05-08T19:00:00Z")},
              {Comment("dangreen", "polyphony:approve concern addressed",       "2026-05-08T20:00:00Z")}
            ]
            """;
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
    }

    [Fact]
    public async Task PollStatus_MagicApprove_DoesNotOverrideNativeChangesRequested()
    {
        // Option B: native CHANGES_REQUESTED is canonical. Magic approve is a
        // deprecated fallback that ONLY resolves "missing" approval in the
        // pending case — it cannot override an explicit reviewer
        // CHANGES_REQUESTED. To clear native changes-requested, the reviewer
        // must dismiss their review or post APPROVED; or, with Option B
        // threads in play, all blocking threads resolved + APPROVED native
        // review is the canonical happy path.
        var (cmd, runner) = CreateCommand();
        var reviews = """
            [
              {"author": {"login": "dangreen"}, "state": "CHANGES_REQUESTED", "submittedAt": "2026-05-08T18:00:00Z"}
            ]
            """;
        var comments = $"[{Comment("dangreen", "polyphony:approve overriding earlier review", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "CHANGES_REQUESTED",
                authorLogin: "dangreen",
                reviewsJson: reviews,
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("changes_requested");
    }

    [Fact]
    public async Task PollStatus_MagicComment_OlderThanAuthorNativeReview_NativeWins()
    {
        // Author posted polyphony:approve, then a real CHANGES_REQUESTED later.
        // Native review wins because it's more recent.
        var (cmd, runner) = CreateCommand();
        var reviews = """
            [
              {"author": {"login": "dangreen"}, "state": "CHANGES_REQUESTED", "submittedAt": "2026-05-08T20:00:00Z"}
            ]
            """;
        var comments = $"[{Comment("dangreen", "polyphony:approve early take", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "CHANGES_REQUESTED",
                authorLogin: "dangreen",
                reviewsJson: reviews,
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("changes_requested");
    }

    [Fact]
    public async Task PollStatus_MagicComment_OtherReviewerChangesRequested_StillBlocks()
    {
        // Author magic-approves; another reviewer separately requested changes.
        // Cross-reviewer aggregation: changes_requested blocks regardless.
        var (cmd, runner) = CreateCommand();
        var reviews = """
            [
              {"author": {"login": "alice"}, "state": "CHANGES_REQUESTED", "submittedAt": "2026-05-08T18:00:00Z"}
            ]
            """;
        var comments = $"[{Comment("dangreen", "polyphony:approve from author", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "CHANGES_REQUESTED",
                authorLogin: "dangreen",
                reviewsJson: reviews,
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("changes_requested");
        result.Policy.MergeAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task PollStatus_NoMagicComment_FallsBackToReviewDecision()
    {
        // Sanity: when no magic comment is present, behavior matches the
        // pre-magic-comment baseline (gh's reviewDecision drives state).
        var (cmd, runner) = CreateCommand();
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED",
                authorLogin: "dangreen",
                commentsJson: "[]"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
    }

    // ---- Option B — review-thread-driven derivation ----

    [Fact]
    public async Task PollStatus_UnresolvedThread_OverridesNativeApproved()
    {
        // Even when the native review decision is APPROVED, an unresolved
        // (and not-outdated) review thread is canonical and blocks merge.
        var (cmd, runner) = CreateCommand();
        StubGhReviewThreads(runner, ThreadsResponse(
            hasNextPage: false,
            ("PRT_1", false, false, "alice", "2026-05-08T19:00:00Z", 1)));
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED",
                authorLogin: "dangreen",
                commentsJson: "[]"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("changes_requested");
        result.Threads.ShouldHaveSingleItem();
        result.Threads[0].IsResolved.ShouldBeFalse();
    }

    [Fact]
    public async Task PollStatus_AllResolvedThreads_SuppressStaleNativeChangesRequested()
    {
        // The loop-bug fix: a stale native CHANGES_REQUESTED that was raised
        // earlier is suppressed once all blocking threads are resolved. With
        // resolved threads + no APPROVED native review, state is "pending"
        // (awaiting a fresh approval), NOT "changes_requested".
        var (cmd, runner) = CreateCommand();
        StubGhReviewThreads(runner, ThreadsResponse(
            hasNextPage: false,
            ("PRT_1", true, false, "alice", "2026-05-08T19:00:00Z", 1)));
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "CHANGES_REQUESTED",
                authorLogin: "dangreen",
                commentsJson: "[]"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("pending");
    }

    [Fact]
    public async Task PollStatus_AllResolvedThreads_PlusApprovedReview_Approved()
    {
        // Resolved threads + APPROVED native review = canonical happy path.
        var (cmd, runner) = CreateCommand();
        StubGhReviewThreads(runner, ThreadsResponse(
            hasNextPage: false,
            ("PRT_1", true, false, "alice", "2026-05-08T19:00:00Z", 1)));
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED",
                authorLogin: "dangreen",
                commentsJson: "[]"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
    }

    [Fact]
    public async Task PollStatus_OutdatedUnresolvedThread_DoesNotBlock()
    {
        // Outdated threads (the underlying lines have been rewritten) are
        // not blocking even if technically "unresolved".
        var (cmd, runner) = CreateCommand();
        StubGhReviewThreads(runner, ThreadsResponse(
            hasNextPage: false,
            ("PRT_1", false, true, "alice", "2026-05-08T19:00:00Z", 1)));
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED",
                authorLogin: "dangreen",
                commentsJson: "[]"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
    }

    [Fact]
    public async Task PollStatus_PaginationOverflow_AppendsFailClosedWarning()
    {
        // When the graphql response indicates more pages exist (>100 threads),
        // we fail closed with a warning rather than silently render a partial
        // view. The current page still drives derivation.
        var (cmd, runner) = CreateCommand();
        StubGhReviewThreads(runner, ThreadsResponse(
            hasNextPage: true,
            ("PRT_1", true, false, "alice", "2026-05-08T19:00:00Z", 1)));
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "APPROVED",
                authorLogin: "dangreen",
                commentsJson: "[]"));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.Warnings.ShouldNotBeNull();
        result.Warnings.ShouldContain(w => w.Contains("page", StringComparison.OrdinalIgnoreCase) || w.Contains("threads", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PollStatus_MagicApprove_BareForm_AppendsDeprecationWarning()
    {
        // Bare polyphony:approve still works as a fallback (single-author
        // convenience) but emits a deprecation warning recommending the
        // SHA-bound canonical form (which self-invalidates on new commits).
        // The warning must include the current head SHA so the user can
        // copy-paste the canonical form.
        var (cmd, runner) = CreateCommand();
        var comments = $"[{Comment("dangreen", "polyphony:approve", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                headRefOid: "deadbeef1234567890abcdef1234567890abcdef",
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Reviewers.ShouldContain(r =>
            r.Identity == "dangreen" && r.Vote == "approved" && r.Source == "magic_comment");
        result.Warnings.ShouldNotBeNull();
        result.Warnings.ShouldContain(w => w.Contains("polyphony:approve", StringComparison.OrdinalIgnoreCase));
        // Warning surfaces the current head SHA so the user can paste the canonical form.
        result.Warnings.ShouldContain(w => w.Contains("deadbeef1234567890abcdef1234567890abcdef", StringComparison.OrdinalIgnoreCase));
    }

    // ----------------------------------------------------------------------
    // SHA-bound magic approve (canonical form, replaces bare-form fallback)
    //
    // Recognized: `polyphony:approve <head-sha>` from the PR author. The
    // SHA pins approval to a specific commit; any new commit silently
    // invalidates the old comment without timestamp comparison. Accepts
    // the conventional 7-char short form through the canonical 40-char
    // form (case-insensitive).
    //
    // Stale SHA-bound comments (SHA does NOT match current head) are
    // ignored entirely — the structural self-invalidation rule. The
    // user's old approval doesn't follow them onto a new commit.
    //
    // The SHA-bound form does NOT emit a deprecation warning. Only the
    // bare form does.
    // ----------------------------------------------------------------------

    private const string CanonicalHeadSha = "deadbeef1234567890abcdef1234567890abcdef";

    [Fact]
    public async Task PollStatus_MagicApprove_ShaBoundMatch_FullSha_ApprovedNoWarning()
    {
        var (cmd, runner) = CreateCommand();
        var comments = $"[{Comment("dangreen", $"polyphony:approve {CanonicalHeadSha}", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                headRefOid: CanonicalHeadSha,
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Reviewers.ShouldContain(r =>
            r.Identity == "dangreen" && r.Vote == "approved" && r.Source == "magic_comment_sha_bound");
        // Canonical form must NOT emit the deprecation warning.
        if (result.Warnings is not null)
        {
            result.Warnings.ShouldNotContain(w => w.Contains("polyphony:approve", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task PollStatus_MagicApprove_ShaBoundMatch_ShortSha7Char_ApprovedNoWarning()
    {
        // 7-char prefix matches the canonical 40-char head SHA.
        var (cmd, runner) = CreateCommand();
        var shortSha = CanonicalHeadSha.Substring(0, 7);
        var comments = $"[{Comment("dangreen", $"polyphony:approve {shortSha}", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                headRefOid: CanonicalHeadSha,
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Reviewers.ShouldContain(r => r.Source == "magic_comment_sha_bound");
    }

    [Fact]
    public async Task PollStatus_MagicApprove_ShaBoundMatch_CaseInsensitive_Approved()
    {
        var (cmd, runner) = CreateCommand();
        var upperSha = CanonicalHeadSha.ToUpperInvariant();
        var comments = $"[{Comment("dangreen", $"polyphony:approve {upperSha}", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                headRefOid: CanonicalHeadSha,
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Reviewers.ShouldContain(r => r.Source == "magic_comment_sha_bound");
    }

    [Fact]
    public async Task PollStatus_MagicApprove_ShaBoundStale_Ignored()
    {
        // Author approved an OLD commit, then a new commit landed. The
        // stale SHA-bound comment must NOT propagate to the new commit —
        // structural self-invalidation. State stays pending.
        var (cmd, runner) = CreateCommand();
        var staleSha = "1111111111111111111111111111111111111111";
        var comments = $"[{Comment("dangreen", $"polyphony:approve {staleSha}", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                headRefOid: CanonicalHeadSha,
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("pending");
        result.Reviewers.ShouldNotContain(r =>
            r.Source == "magic_comment" || r.Source == "magic_comment_sha_bound");
    }

    [Fact]
    public async Task PollStatus_MagicApprove_BareThenShaBoundMatch_NoWarning()
    {
        // Author posted bare approve earlier, then the canonical SHA-bound
        // form. The newer SHA-bound wins; no deprecation warning.
        var (cmd, runner) = CreateCommand();
        var comments = $$"""
            [
              {{Comment("dangreen", "polyphony:approve", "2026-05-08T19:00:00Z")}},
              {{Comment("dangreen", $"polyphony:approve {CanonicalHeadSha}", "2026-05-08T20:00:00Z")}}
            ]
            """;
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                headRefOid: CanonicalHeadSha,
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Reviewers.ShouldContain(r => r.Source == "magic_comment_sha_bound");
        if (result.Warnings is not null)
        {
            result.Warnings.ShouldNotContain(w => w.Contains("polyphony:approve", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task PollStatus_MagicApprove_ShaBoundMatchThenBare_BareWinsWithWarning()
    {
        // Author posted SHA-bound earlier, then bare later. The newer bare
        // form wins (most-recent-wins) and triggers the deprecation warning.
        var (cmd, runner) = CreateCommand();
        var comments = $$"""
            [
              {{Comment("dangreen", $"polyphony:approve {CanonicalHeadSha}", "2026-05-08T19:00:00Z")}},
              {{Comment("dangreen", "polyphony:approve",                     "2026-05-08T20:00:00Z")}}
            ]
            """;
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                headRefOid: CanonicalHeadSha,
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Reviewers.ShouldContain(r => r.Source == "magic_comment");
        result.Warnings.ShouldNotBeNull();
        result.Warnings.ShouldContain(w => w.Contains("polyphony:approve", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PollStatus_MagicApprove_ShaBoundStalePlusBare_BareWinsWithWarning()
    {
        // Stale SHA-bound is silently ignored, but the bare form still
        // counts as approval. Warning is emitted (bare contributed).
        var (cmd, runner) = CreateCommand();
        var staleSha = "1111111111111111111111111111111111111111";
        var comments = $$"""
            [
              {{Comment("dangreen", $"polyphony:approve {staleSha}",  "2026-05-08T19:00:00Z")}},
              {{Comment("dangreen", "polyphony:approve",              "2026-05-08T20:00:00Z")}}
            ]
            """;
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                headRefOid: CanonicalHeadSha,
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("approved");
        result.Reviewers.ShouldContain(r => r.Source == "magic_comment");
        result.Warnings.ShouldNotBeNull();
    }

    [Fact]
    public async Task PollStatus_MagicApprove_ShaBoundFromNonAuthor_Ignored()
    {
        // SHA-bound from someone other than the PR author is ignored —
        // single-author rule still applies. Non-authors should leave a
        // native review.
        var (cmd, runner) = CreateCommand();
        var comments = $"[{Comment("alice", $"polyphony:approve {CanonicalHeadSha}", "2026-05-08T19:00:00Z")}]";
        StubGhPrView(runner, "owner/repo", 42,
            PrJson(42, "OPEN", "REVIEW_REQUIRED",
                headRefOid: CanonicalHeadSha,
                authorLogin: "dangreen",
                commentsJson: comments));
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.PollStatus(prUrl: "https://github.com/owner/repo/pull/42"));
        var result = Parse(output);
        result.State.ShouldBe("pending");
        result.Reviewers.ShouldNotContain(r =>
            r.Source == "magic_comment" || r.Source == "magic_comment_sha_bound");
    }
}
