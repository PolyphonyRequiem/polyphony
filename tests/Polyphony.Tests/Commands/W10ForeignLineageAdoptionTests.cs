using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W10 (AB#3291): end-to-end coverage that the lineage check fires
/// on the adoption path. One representative ensure-* verb and one
/// representative open-* verb prove the wiring; <see cref="BranchLineageGuardTests"/>
/// and <see cref="PrLineageGuardTests"/> cover the matrix at unit level.
/// </summary>
public sealed class W10ForeignLineageAdoptionTests : CommandTestBase, IDisposable
{
    private readonly string _tempDir;
    private readonly JournalStore _journal;

    public W10ForeignLineageAdoptionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-w10-foreign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _journal = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
    }

    public override void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private async Task RecordForeignEnsureAsync(string foreignRunId, string target, string action)
    {
        var actionId = await _journal.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = foreignRunId,
                RootId = 100,
                WorkItemId = 200,
                Action = action,
                Target = target,
                StartedAt = 1_800_000_000_000,
            },
            CancellationToken.None);
        await _journal.RecordEndAsync(actionId, JournalOutcome.Success, null, null, null, null, CancellationToken.None);
    }

    [Fact]
    public async Task EnsureImpl_BranchExistsRemotelyOwnedByForeignLineage_Refuses()
    {
        const string branch = "impl/100-200";
        await RecordForeignEnsureAsync("01JCFOREIGN", branch, "branch_ensure_impl");

        var runner = new FakeProcessRunner();
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", branch],
            new ProcessResult(0, $"abc123\trefs/heads/{branch}\n", ""));
        runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(1, "", "fatal: needed a single revision"));

        var cmd = CreateBranchCommands(runner, runId: "01JCMINE");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 100, itemId: 200, mgPath: "core"));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureImplResult)!;
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("foreign_lineage");
        result.Error.ShouldContain("01JCFOREIGN");
    }

    [Fact]
    public async Task EnsureImpl_BranchExistsButOwnedByCurrentLineage_Allows()
    {
        const string branch = "impl/100-200";
        const string baseBranch = "mg/100_core";
        await RecordForeignEnsureAsync("01JCMINE", branch, "branch_ensure_impl");

        var runner = new FakeProcessRunner();
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", branch],
            new ProcessResult(0, $"abc123\trefs/heads/{branch}\n", ""));
        runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(1, "", "fatal: needed a single revision"));
        runner.WhenExact("git", ["fetch", "origin", branch],
            new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["checkout", "--track", $"origin/{branch}"],
            new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", baseBranch],
            new ProcessResult(0, $"def456\trefs/heads/{baseBranch}\n", ""));
        runner.WhenExact("git", ["rev-parse", $"refs/heads/{branch}"],
            new ProcessResult(0, "abc123\n", ""));
        runner.WhenExact("git", ["rev-parse", "--abbrev-ref", "HEAD"],
            new ProcessResult(0, $"{branch}\n", ""));

        var cmd = CreateBranchCommands(runner, runId: "01JCMINE");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureImpl(rootId: 100, itemId: 200, mgPath: "core"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureImplResult)!;
        result.Error.ShouldBeNull();
        result.Action.ShouldBe("checked_out");
    }

    [Fact]
    public async Task OpenImplPr_ReusesExistingPrOwnedByForeignLineage_Refuses()
    {
        const string headBranch = "impl/100-200";
        const string baseBranch = "mg/100_core";
        var pairTarget = $"{headBranch}->{baseBranch}";
        await RecordForeignEnsureAsync("01JCFOREIGN", pairTarget, "pr_open_impl_pr");

        var runner = new FakeProcessRunner();
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, "git@github.com:org/repo.git\n", ""));
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{headBranch}"],
            new ProcessResult(0, $"abc123\trefs/heads/{headBranch}\n", ""));
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", $"refs/heads/{baseBranch}"],
            new ProcessResult(0, $"def456\trefs/heads/{baseBranch}\n", ""));
        runner.WhenStartsWith("twig", ["show"], new ProcessResult(0, """{"title":"x","id":200}""", ""));
        runner.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(0, $$"""[{"number":42,"url":"https://github.com/org/repo/pull/42","headRefName":"{{headBranch}}"}]""", ""));
        // gh pr view returns body WITHOUT the run-id marker so the journal
        // is the corroborating source; the journal has a foreign row.
        runner.WhenStartsWith("gh", ["pr", "view", "42"],
            new ProcessResult(0, """{"body":"## Some PR\n\nNo marker.","title":"x"}""", ""));

        var cmd = CreatePrCommands(runner, runId: "01JCMINE");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.OpenImplPr(rootId: 100, itemId: 200, mgPath: "core"));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrOpenImplResult)!;
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("foreign_lineage");
    }

    private BranchCommands CreateBranchCommands(FakeProcessRunner runner, string runId)
    {
        var twig = new TwigClient(runner);
        var config = new ProcessConfigBuilder()
            .WithType("Issue", ["plannable", "implementable"], new Dictionary<string, string>())
            .Build();
        var store = new SqliteCacheStore("Data Source=:memory:");
        var repo = new SqliteWorkItemRepository(store, new WorkItemMapper());
        var walker = new HierarchyWalker(config, repo);
        var validator = new TransitionValidator(config);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return new BranchCommands(
            twig, walker, repo, validator, git, config,
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            new Polyphony.Sdlc.Observers.PullRequestReader(gh, null),
            JournalTestSupport.CreateRunContext(runId),
            JournalTestSupport.CreateDecorator(),
            _journal);
    }

    private PrCommands CreatePrCommands(FakeProcessRunner runner, string runId)
    {
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return new PrCommands(
            git, gh, twig, Repository, Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            JournalTestSupport.CreateRunContext(runId),
            JournalTestSupport.CreateDecorator(),
            journalStore: _journal);
    }
}
