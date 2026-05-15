using System.Net;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony pr get-comments-ado</c>. Stubs
/// <see cref="IAdoClient"/> directly (the verb only consumes one method on
/// it) and asserts on the <see cref="PrGetCommentsAdoResult"/> envelope.
/// Always exits 0 — error states surface in <c>error_code</c>.
///
/// <para>Mirrors the <see cref="PrCommandsPostCommentAdoTests"/> pattern:
/// happy path, argument validation, ADO error propagation, JSON contract,
/// plus the verb-specific filter axes (<c>--include-resolved</c>,
/// <c>--since</c>, system / tombstoned content).</para>
/// </summary>
public sealed class PrCommandsGetCommentsAdoTests : CommandTestBase
{
    private const string Org = "myorg";
    private const string Project = "myproj";
    private const string Repo = "myrepo";
    private const int PrId = 42;

    private (PrCommands Command, FakeAdoClient Ado) CreateCommand(FakeAdoClient? ado = null)
    {
        ado ??= new FakeAdoClient();
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(
            git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            ado);
        return (cmd, ado);
    }

    private static PrGetCommentsAdoResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrGetCommentsAdoResult)!;

    private static AdoPullRequestThread Thread(
        int id,
        AdoPullRequestComment[] comments,
        string status = "active",
        string? filePath = null,
        int? line = null)
        => new()
        {
            Id = id,
            Status = status,
            IsResolved = AdoClient.IsResolvedThreadStatus(status),
            FilePath = filePath,
            Line = line,
            Comments = comments,
        };

    private static AdoPullRequestComment Comment(
        int id,
        string author = "Jane Doe",
        string body = "Looks good",
        int parentCommentId = 0,
        DateTime? publishedAt = null,
        DateTime? lastUpdatedAt = null,
        string commentType = "text")
        => new()
        {
            Id = id,
            ParentCommentId = parentCommentId,
            Author = author,
            Body = body,
            PublishedAt = publishedAt ?? DateTime.UtcNow,
            LastUpdatedAt = lastUpdatedAt ?? publishedAt ?? DateTime.UtcNow,
            CommentType = commentType,
        };

    // ─── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetCommentsAdo_HappyPath_FlattensThreadsToCommentRows()
    {
        var t1Published = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var t2Published = new DateTime(2024, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>
        {
            Thread(101, new[] { Comment(1, author: "Reviewer A", body: "Consider extracting this", publishedAt: t1Published), Comment(2, author: "Author", body: "Will do", parentCommentId: 1, publishedAt: t2Published) }, status: "active", filePath: "/src/Foo.cs", line: 10),
            Thread(102, new[] { Comment(3, author: "Reviewer B", body: "Top-level PR comment", publishedAt: t2Published) }, status: "active"),
        };

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.PrNumber.ShouldBe(PrId);
        result.RepoSlug.ShouldBe("myorg/myproj/myrepo");
        result.PrUrl.ShouldBe("https://dev.azure.com/myorg/myproj/_git/myrepo/pullrequest/42");
        result.Count.ShouldBe(3);
        result.Comments.Count.ShouldBe(3);

        // Thread context denormalised onto each row.
        result.Comments[0].ThreadId.ShouldBe(101);
        result.Comments[0].FilePath.ShouldBe("/src/Foo.cs");
        result.Comments[0].Line.ShouldBe(10);
        result.Comments[0].Author.ShouldBe("Reviewer A");
        result.Comments[0].Body.ShouldBe("Consider extracting this");
        result.Comments[0].ParentCommentId.ShouldBe(0);
        result.Comments[0].IsResolved.ShouldBeFalse();
        result.Comments[0].IsOutdated.ShouldBeFalse();
        result.Comments[0].ThreadStatus.ShouldBe("active");
        result.Comments[0].CommentType.ShouldBe("text");

        // Reply preserves parent_comment_id and shares the thread context.
        result.Comments[1].Id.ShouldBe(2);
        result.Comments[1].ThreadId.ShouldBe(101);
        result.Comments[1].ParentCommentId.ShouldBe(1);
        result.Comments[1].FilePath.ShouldBe("/src/Foo.cs");

        // Top-level PR comment has no file/line.
        result.Comments[2].ThreadId.ShouldBe(102);
        result.Comments[2].FilePath.ShouldBeNull();
        result.Comments[2].Line.ShouldBeNull();
        result.Comments[2].Body.ShouldBe("Top-level PR comment");

        result.Error.ShouldBeNull();
        result.ErrorCode.ShouldBeNull();

        ado.ListThreadsCallCount.ShouldBe(1);
        ado.LastOrganization.ShouldBe(Org);
        ado.LastProject.ShouldBe(Project);
        ado.LastRepository.ShouldBe(Repo);
        ado.LastPrId.ShouldBe(PrId);
    }

    [Fact]
    public async Task GetCommentsAdo_EmptyThreads_ReturnsCountZero()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Count.ShouldBe(0);
        result.Comments.ShouldBeEmpty();
        result.PrNumber.ShouldBe(PrId);
        result.Error.ShouldBeNull();
        result.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task GetCommentsAdo_AcceptsRepositoryGuid()
    {
        var (cmd, ado) = CreateCommand();
        var repoGuid = "00000000-0000-0000-0000-000000000001";
        ado.Threads = new List<AdoPullRequestThread>();

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, repoGuid, PrId));
        var result = Parse(output);

        result.RepoSlug.ShouldBe($"myorg/myproj/{repoGuid}");
        ado.LastRepository.ShouldBe(repoGuid);
    }

    // ─── --include-resolved filter ───────────────────────────────────────

    [Fact]
    public async Task GetCommentsAdo_DefaultExcludesResolvedThreads()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>
        {
            Thread(1, new[] { Comment(10, body: "open feedback") }, status: "active"),
            Thread(2, new[] { Comment(20, body: "fixed feedback") }, status: "fixed"),
            Thread(3, new[] { Comment(30, body: "wontfix feedback") }, status: "wontFix"),
            Thread(4, new[] { Comment(40, body: "closed feedback") }, status: "closed"),
            Thread(5, new[] { Comment(50, body: "bydesign feedback") }, status: "byDesign"),
        };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.Count.ShouldBe(1);
        result.Comments[0].Body.ShouldBe("open feedback");
    }

    [Fact]
    public async Task GetCommentsAdo_IncludeResolvedTrue_ReturnsAllThreads()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>
        {
            Thread(1, new[] { Comment(10, body: "open") }, status: "active"),
            Thread(2, new[] { Comment(20, body: "fixed") }, status: "fixed"),
            Thread(3, new[] { Comment(30, body: "closed") }, status: "closed"),
        };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId, includeResolved: true));
        var result = Parse(output);

        result.Count.ShouldBe(3);
        result.Comments.Select(c => c.Body).ShouldBe(["open", "fixed", "closed"]);
        result.Comments[1].IsResolved.ShouldBeTrue();
        result.Comments[1].ThreadStatus.ShouldBe("fixed");
        result.Comments[2].IsResolved.ShouldBeTrue();
    }

    [Fact]
    public async Task GetCommentsAdo_PendingThread_NotTreatedAsResolved()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>
        {
            Thread(1, new[] { Comment(1, body: "pending feedback") }, status: "pending"),
        };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.Count.ShouldBe(1);
        result.Comments[0].IsResolved.ShouldBeFalse();
    }

    // ─── --since filter ──────────────────────────────────────────────────

    [Fact]
    public async Task GetCommentsAdo_SinceFilter_DropsOlderComments()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>
        {
            Thread(1, new[] { Comment(10, body: "before", publishedAt: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Comment(11, body: "after",  publishedAt: new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)) }, status: "active"),
        };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId, since: "2024-03-01T00:00:00Z"));
        var result = Parse(output);

        result.Count.ShouldBe(1);
        result.Comments[0].Body.ShouldBe("after");
    }

    [Fact]
    public async Task GetCommentsAdo_SinceFilter_AcceptsLocalTimestamp_NormalisesToUtc()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>
        {
            Thread(1, new[] { Comment(10, body: "old", publishedAt: new DateTime(2023, 12, 31, 23, 0, 0, DateTimeKind.Utc)), Comment(11, body: "new", publishedAt: new DateTime(2024, 1, 1, 6, 0, 0, DateTimeKind.Utc)) }, status: "active"),
        };

        // Local-time string with explicit offset — verb must round-trip to UTC.
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId, since: "2024-01-01T00:00:00+00:00"));
        var result = Parse(output);

        result.Count.ShouldBe(1);
        result.Comments[0].Body.ShouldBe("new");
    }

    [Fact]
    public async Task GetCommentsAdo_SinceFilter_EmptyStringDisablesFilter()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>
        {
            Thread(1, new[] { Comment(10, body: "ancient", publishedAt: new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)) }, status: "active"),
        };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId, since: ""));
        var result = Parse(output);

        result.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2024-13-99")]
    [InlineData("yesterday")]
    public async Task GetCommentsAdo_SinceFilter_InvalidString_EmitsInvalidArgument(string since)
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId, since: since));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("since");
        ado.ListThreadsCallCount.ShouldBe(0);
    }

    // ─── Argument validation ─────────────────────────────────────────────

    [Theory]
    [InlineData("   ",    Project, Repo)]
    [InlineData(Org,      "   ",   Repo)]
    [InlineData(Org,      Project, "   ")]
    public async Task GetCommentsAdo_WhitespaceRequiredArgument_EmitsInvalidArgument(
        string organization, string project, string repository)
    {
        var (cmd, ado) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(organization, project, repository, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("organization");
        result.Count.ShouldBe(0);
        ado.ListThreadsCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("",       Project, Repo,    "--organization")]
    [InlineData(Org,      "",      Repo,    "--project")]
    [InlineData(Org,      Project, "",      "--repository")]
    public async Task GetCommentsAdo_EmptyRequiredArgument_EmitsInvalidArgument(
        string organization, string project, string repository, string missingFlag)
    {
        var (cmd, ado) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(organization, project, repository, PrId));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("pr get-comments-ado");
        envelope.MissingArgs.ShouldContain(missingFlag);
        ado.ListThreadsCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-42)]
    public async Task GetCommentsAdo_NonPositivePrNumber_EmitsInvalidArgument(int prNumber)
    {
        var (cmd, ado) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, prNumber));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_argument");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("prNumber");
        result.PrNumber.ShouldBe(prNumber);
        ado.ListThreadsCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetCommentsAdo_InvalidArgument_RepoSlugBlankWhenSlugComponentMissing()
    {
        // With the Move #2 halt contract, an empty required arg short-circuits
        // before slug/url are computed; verify the halt envelope shape.
        var (cmd, _) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, "", Repo, PrId));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Verb.ShouldBe("pr get-comments-ado");
        envelope.MissingArgs.ShouldContain("--project");
    }

    // ─── ADO error envelopes ─────────────────────────────────────────────

    [Fact]
    public async Task GetCommentsAdo_AdoReturnsNull_EmitsPrNotFound()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = null; // mimics IAdoClient returning null on 404

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("pr_not_found");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("not found");
        result.Count.ShouldBe(0);
        result.Comments.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetCommentsAdo_AdoTimeout_EmitsAdoTimeoutErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnListThreads = new TimeoutException("ADO request timed out after 3 attempt(s).");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.ErrorCode.ShouldBe("ado_timeout");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("timed out");
    }

    [Fact]
    public async Task GetCommentsAdo_NoPat_EmitsNoPatErrorCode()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnListThreads = new InvalidOperationException(
            "No ADO PAT configured (set AZURE_DEVOPS_EXT_PAT or run 'az devops login').");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.ErrorCode.ShouldBe("no_pat");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("AZURE_DEVOPS_EXT_PAT");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "no_pat")]
    [InlineData(HttpStatusCode.Forbidden,    "no_pat")]
    [InlineData(HttpStatusCode.BadGateway,   "ado_failed")]
    [InlineData(HttpStatusCode.InternalServerError, "ado_failed")]
    public async Task GetCommentsAdo_HttpStatusMappedToErrorCode(HttpStatusCode status, string expected)
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnListThreads = new HttpRequestException(
            $"ADO request failed: HTTP {(int)status}", inner: null, statusCode: status);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.ErrorCode.ShouldBe(expected);
    }

    [Fact]
    public async Task GetCommentsAdo_HttpExceptionWithoutStatus_EmitsAdoFailed()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnListThreads = new HttpRequestException("network broken");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.ErrorCode.ShouldBe("ado_failed");
    }

    [Fact]
    public async Task GetCommentsAdo_UnexpectedException_EmitsAdoFailed()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnListThreads = new InvalidDataException("malformed body");

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));
        var result = Parse(output);

        result.ErrorCode.ShouldBe("ado_failed");
        result.Error!.ShouldContain("malformed");
    }

    [Fact]
    public async Task GetCommentsAdo_OperationCanceled_PropagatesNoEnvelope()
    {
        var (cmd, ado) = CreateCommand();
        ado.ThrowOnListThreads = new OperationCanceledException();

        await Should.ThrowAsync<OperationCanceledException>(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));
    }

    [Fact]
    public async Task GetCommentsAdo_NoAdoClientConfigured_EmitsAdoFailed()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(
            git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            ado: null);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("ado_failed");
        result.Error!.ShouldContain("IAdoClient");
    }

    // ─── JSON contract ───────────────────────────────────────────────────

    [Fact]
    public async Task GetCommentsAdo_JsonContract_IsSnakeCase()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>
        {
            Thread(1, new[] { Comment(7, body: "x") }, filePath: "/x.cs", line: 5),
        };

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));

        output.ShouldContain("\"pr_number\"");
        output.ShouldContain("\"repo_slug\"");
        output.ShouldContain("\"pr_url\"");
        output.ShouldContain("\"count\"");
        output.ShouldContain("\"comments\"");
        output.ShouldContain("\"thread_id\"");
        output.ShouldContain("\"parent_comment_id\"");
        output.ShouldContain("\"file_path\"");
        output.ShouldContain("\"published_at\"");
        output.ShouldContain("\"last_updated_at\"");
        output.ShouldContain("\"is_resolved\"");
        output.ShouldContain("\"is_outdated\"");
        output.ShouldContain("\"thread_status\"");
        output.ShouldContain("\"comment_type\"");
        // No PascalCase leakage.
        output.ShouldNotContain("\"PrNumber\"");
        output.ShouldNotContain("\"ThreadId\"");
        output.ShouldNotContain("\"FilePath\"");
        output.ShouldNotContain("\"IsResolved\"");
    }

    [Fact]
    public async Task GetCommentsAdo_JsonContract_ErrorFieldsOmittedOnSuccess()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = new List<AdoPullRequestThread>();

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));

        output.ShouldNotContain("\"error\"");
        output.ShouldNotContain("\"error_code\"");
    }

    [Fact]
    public async Task GetCommentsAdo_JsonContract_OnErrorPathIncludesErrorFields()
    {
        var (cmd, ado) = CreateCommand();
        ado.Threads = null;

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.GetCommentsAdo(Org, Project, Repo, PrId));

        output.ShouldContain("\"error\"");
        output.ShouldContain("\"error_code\"");
    }

    [Fact]
    public async Task GetCommentsAdo_AlwaysReturnsExitCodeSuccess_AllPaths()
    {
        // Move #2: paths that pass an empty required arg now halt with
        // RoutingFailure before reaching the verb body, so they're excluded
        // here — see GetCommentsAdo_EmptyRequiredArgument_EmitsInvalidArgument.
        var (cmd, _) = CreateCommand();

        var paths = new (FakeAdoClient Ado, Func<Task<int>> Invoke)[]
        {
            (new FakeAdoClient { Threads = new List<AdoPullRequestThread>() },
                () => cmd.GetCommentsAdo(Org, Project, Repo, PrId)),
            (new FakeAdoClient(),
                () => cmd.GetCommentsAdo(Org, Project, Repo, 0)),
            (new FakeAdoClient(),
                () => cmd.GetCommentsAdo(Org, Project, Repo, PrId, since: "garbage")),
            (new FakeAdoClient { Threads = null },
                () => cmd.GetCommentsAdo(Org, Project, Repo, PrId)),
            (new FakeAdoClient { ThrowOnListThreads = new TimeoutException() },
                () => cmd.GetCommentsAdo(Org, Project, Repo, PrId)),
            (new FakeAdoClient { ThrowOnListThreads = new InvalidOperationException("x") },
                () => cmd.GetCommentsAdo(Org, Project, Repo, PrId)),
            (new FakeAdoClient { ThrowOnListThreads = new HttpRequestException("x", null, HttpStatusCode.BadGateway) },
                () => cmd.GetCommentsAdo(Org, Project, Repo, PrId)),
            (new FakeAdoClient { ThrowOnListThreads = new HttpRequestException("x", null, HttpStatusCode.Unauthorized) },
                () => cmd.GetCommentsAdo(Org, Project, Repo, PrId)),
            (new FakeAdoClient { ThrowOnListThreads = new InvalidDataException("x") },
                () => cmd.GetCommentsAdo(Org, Project, Repo, PrId)),
        };

        foreach (var (_, invoke) in paths)
        {
            var (exit, _) = await CaptureConsoleAsync(invoke);
            exit.ShouldBe(ExitCodes.Success);
        }
    }

    // ─── Test fake ───────────────────────────────────────────────────────

    private sealed class FakeAdoClient : IAdoClient
    {
        public IReadOnlyList<AdoPullRequestThread>? Threads { get; set; } =
            Array.Empty<AdoPullRequestThread>();
        public Exception? ThrowOnListThreads { get; set; }
        public int ListThreadsCallCount { get; private set; }
        public string? LastOrganization { get; private set; }
        public string? LastProject { get; private set; }
        public string? LastRepository { get; private set; }
        public int LastPrId { get; private set; }

        public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
            string organization, string project, string repository,
            AdoPullRequestStatus status = AdoPullRequestStatus.Active,
            string? sourceBranch = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequest?> GetPullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequest?> CreatePullRequestAsync(
            string organization, string project, string repository,
            string sourceBranch, string targetBranch, string title,
            string description, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
            string organization, string project, string repositoryId,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> SetPullRequestVoteAsync(
            string organization, string project, string repository,
            int pullRequestId, string reviewerId, int vote,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string lastMergeSourceCommitSha,
            AdoMergeStrategy mergeStrategy, bool deleteSourceBranch,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<AdoCreateThreadResult?> CreatePullRequestCommentThreadAsync(
            string organization, string project, string repository,
            int pullRequestId, string commentBody,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequestThread>?> ListPullRequestThreadsAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
        {
            ListThreadsCallCount++;
            LastOrganization = organization;
            LastProject = project;
            LastRepository = repository;
            LastPrId = pullRequestId;
            if (ThrowOnListThreads is not null) throw ThrowOnListThreads;
            return Task.FromResult(Threads);
        }

        public Task<AdoEvidenceFloorRead> GetPullRequestEvidenceFloorAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<AdoPullRequestChangedFile>?> GetPullRequestFilesAsync(
            string organization, string project, string repository,
            int pullRequestId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> EditPullRequestBodyAsync(
            string organization, string project, string repository,
            int pullRequestId, string body, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> ClosePullRequestAsync(
            string organization, string project, string repository,
            int pullRequestId, string commentBeforeClose, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
