using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Twig.Domain.Services;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class PrCommandsJournalTests : CommandTestBase
{
    private readonly string _scratchRoot = Path.Combine(AppContext.BaseDirectory, "pr-journal-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _createdDirs = [];

    [Fact]
    public async Task CreateFeaturePr_Created_WritesJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100-x", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubTwigShowTree(runner, 100, "Add cool thing");
        StubPrListEmpty(runner);
        StubPrCreate(runner, "https://github.com/PolyphonyRequiem/polyphony/pull/42");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.CreateFeaturePr(workItem: 100, featureBranch: "feature/100-x", targetBranch: "main"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "pr_create_feature_pr" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("feature/100-x->main");
        entry.Outcome.ShouldBe(JournalOutcome.Success);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.PrCreateFeaturePrPayload);
        payload.ShouldNotBeNull();
        payload.WorkItemId.ShouldBe(100);
        payload.FeatureBranch.ShouldBe("feature/100-x");
        payload.PrNumber.ShouldBe(42);
        payload.WasMutated.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenMergeGroupPr_ReusedPr_WritesNoOpJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/mg/100_core", exists: true);
        StubLsRemoteHas(runner, "origin", "refs/heads/feature/100", exists: true);
        StubGitRemoteOrigin(runner, "https://github.com/PolyphonyRequiem/polyphony.git");
        StubPrListExisting(runner, 99, "https://github.com/PolyphonyRequiem/polyphony/pull/99", "mg/100_core");

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.OpenMergeGroupPr(rootId: 100, mgPath: "core"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "pr_open_mg_pr" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("mg/100_core->feature/100");
        entry.Outcome.ShouldBe(JournalOutcome.NoOp);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.PrOpenMergeGroupPrPayload);
        payload.ShouldNotBeNull();
        payload.MergeGroupPath.ShouldBe("core");
        payload.PrNumber.ShouldBe(99);
        payload.WasMutated.ShouldBeFalse();
    }

    [Fact]
    public async Task OpenMergeGroupPr_MissingHead_WritesFailureJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        StubLsRemoteHas(runner, "origin", "refs/heads/mg/100_core", exists: false);

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.OpenMergeGroupPr(rootId: 100, mgPath: "core"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "pr_open_mg_pr" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Outcome.ShouldBe(JournalOutcome.Failure);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.PrOpenMergeGroupPrPayload);
        payload.ShouldNotBeNull();
        payload.Succeeded.ShouldBeFalse();
        payload.Error.ShouldNotBeNull();
        payload.Error.ShouldContain("head branch");
    }

    [Fact]
    public async Task MergeEvidencePr_GithubMerge_WritesSuccessJournalEntry()
    {
        var (cmd, runner, store) = CreateCommand();
        runner.WhenStartsWith("gh", ["pr", "merge"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("gh", ["pr", "view"], new ProcessResult(0,
            """{"number":42,"state":"MERGED","mergeCommit":{"oid":"deadbeef"},"headRefName":"evidence/100","headRefOid":"x"}""", ""));

        var (exitCode, _) = await CaptureConsoleAsync(() => cmd.MergeEvidencePr(
            prNumber: 42,
            prUrl: "https://github.com/PolyphonyRequiem/polyphony/pull/42",
            platform: "github",
            repository: "PolyphonyRequiem/polyphony"));
        var entries = await store.QueryAsync(new JournalQuery { Action = "pr_merge_evidence_pr" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        var entry = entries[0];
        entry.Target.ShouldBe("https://github.com/PolyphonyRequiem/polyphony/pull/42");
        entry.Outcome.ShouldBe(JournalOutcome.Success);
        var payload = JsonSerializer.Deserialize(entry.PayloadJson!, PolyphonyJsonContext.Default.PrMergeEvidencePrPayload);
        payload.ShouldNotBeNull();
        payload.PrNumber.ShouldBe(42);
        payload.MergeCommit.ShouldBe("deadbeef");
        payload.WasMutated.ShouldBeTrue();
        payload.AlreadyMerged.ShouldBeFalse();
    }

    private (PrCommands Command, FakeProcessRunner Runner, JournalStore Store) CreateCommand(ProcessConfig? cfg = null, string runId = "run-pr-journal")
    {
        var scratchDir = Path.Combine(_scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(scratchDir, ".polyphony-state"));
        _createdDirs.Add(scratchDir);

        var store = new JournalStore(Path.Combine(scratchDir, ".polyphony-state", "journal.db"));
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var cmd = new PrCommands(
            git,
            gh,
            twig,
            Repository,
            cfg ?? Config,
            new Polyphony.Locking.RunLockStore(),
            new Polyphony.Locking.RunLockPathResolver(git),
            new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(git),
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            new RunContext(runId),
            new JournaledActionDecorator(store));
        return (cmd, runner, store);
    }

    private static void StubGitRemoteOrigin(FakeProcessRunner runner, string url)
        => runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(0, url + "\n", ""));

    private static void StubLsRemoteHas(FakeProcessRunner runner, string remote, string pattern, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", remote, pattern],
            new ProcessResult(0, exists ? "abc123\trefs/heads/whatever\n" : "", ""));

    private static void StubTwigShowTree(FakeProcessRunner runner, int id, string? title)
    {
        var json = title is null ? "" : $$"""{"title":"{{title}}","id":{{id}}}""";
        runner.WhenExact("twig", ["show", id.ToString(), "--tree", "--output", "json"],
            new ProcessResult(title is null ? 1 : 0, json, ""));
    }

    private static void StubPrListEmpty(FakeProcessRunner runner)
        => runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));

    private static void StubPrListExisting(FakeProcessRunner runner, int number, string url, string headRefName)
        => runner.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(0, $$"""[{"number":{{number}},"url":"{{url}}","headRefName":"{{headRefName}}"}]""", ""));

    private static void StubPrCreate(FakeProcessRunner runner, string url)
        => runner.WhenStartsWith("gh", ["pr", "create"], new ProcessResult(0, url + "\n", ""));

    public override void Dispose()
    {
        base.Dispose();
        foreach (var dir in _createdDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
