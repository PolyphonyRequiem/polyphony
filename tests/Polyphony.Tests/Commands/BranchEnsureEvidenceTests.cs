using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Twig.Domain.Services;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony branch ensure-evidence-branch</c>. Idempotent
/// evidence-branch materialization with base = <c>feature/{apex_id}</c>
/// (or a custom <c>--from-ref</c>). Mirrors
/// <see cref="BranchCommandsEnsureImplTests"/> with the orphan-form
/// collapse and from-ref override layered on top.
/// </summary>
public sealed class BranchEnsureEvidenceTests : CommandTestBase
{
    private static (BranchCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
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
        return (new BranchCommands(twig, walker, repo, validator, git, config, new Polyphony.Sdlc.Observers.RepoIdentityResolver(git), new Polyphony.Sdlc.Observers.PullRequestReader(gh, null)), runner);
    }

    private static void StubLsRemote(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["ls-remote", "--heads", "origin", branch],
            new ProcessResult(0, exists ? $"abc123\trefs/heads/{branch}\n" : "", ""));

    private static void StubLocalBranchExists(FakeProcessRunner runner, string branch, bool exists)
        => runner.WhenExact("git", ["rev-parse", "--verify", $"refs/heads/{branch}"],
            new ProcessResult(exists ? 0 : 1, exists ? "abc123\n" : "", exists ? "" : "fatal: needed a single revision"));

    private static void StubCheckout(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", branch], new ProcessResult(0, "", ""));

    private static void StubCheckoutTracking(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["checkout", "--track", $"origin/{branch}"], new ProcessResult(0, "", ""));

    private static void StubCreateBranch(FakeProcessRunner runner, string branch, string startPoint)
        => runner.WhenExact("git", ["checkout", "-b", branch, startPoint], new ProcessResult(0, "", ""));

    private static void StubPush(FakeProcessRunner runner, string branch)
        => runner.WhenExact("git", ["push", "-u", "origin", branch], new ProcessResult(0, "", ""));

    private static void StubFetch(FakeProcessRunner runner, string refspec)
        => runner.WhenExact("git", ["fetch", "origin", refspec], new ProcessResult(0, "", ""));

    [Fact]
    public async Task EnsureEvidence_InvalidWorkItemId_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 0));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Action.ShouldBe("error");
        result.Error!.ShouldContain("workItemId");
    }

    [Fact]
    public async Task EnsureEvidence_NegativeApexId_ReturnsConfigError()
    {
        // Negative apex is rejected so callers don't get a silent collapse
        // to orphan when they fat-finger a sign.
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: -5));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Error!.ShouldContain("apexId");
    }

    [Fact]
    public async Task EnsureEvidence_AllMissing_CombinedForm_CreatesAndPushes()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: false);
        StubLocalBranchExists(runner, "evidence/100-200", exists: false);
        StubLsRemote(runner, "feature/100", exists: true);
        StubLocalBranchExists(runner, "feature/100", exists: true);
        StubCreateBranch(runner, "evidence/100-200", "feature/100");
        StubPush(runner, "evidence/100-200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Branch.ShouldBe("evidence/100-200");
        result.BaseBranch.ShouldBe("feature/100");
        result.Action.ShouldBe("created");
        result.Pushed.ShouldBeTrue();
        result.CreatedFrom.ShouldBe("feature/100");
        result.ApexId.ShouldBe(100);
        result.ItemId.ShouldBe(200);
        result.Orphan.ShouldBeFalse();
        result.FromRef.ShouldBe("");
    }

    [Fact]
    public async Task EnsureEvidence_AllMissing_OrphanForm_CreatesEvidenceSlashItem()
    {
        // No --apex-id supplied → resolved apex == workItemId → orphan
        // branch name evidence/{item}, base feature/{item}.
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/200", exists: false);
        StubLocalBranchExists(runner, "evidence/200", exists: false);
        StubLsRemote(runner, "feature/200", exists: true);
        StubLocalBranchExists(runner, "feature/200", exists: true);
        StubCreateBranch(runner, "evidence/200", "feature/200");
        StubPush(runner, "evidence/200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Branch.ShouldBe("evidence/200");
        result.BaseBranch.ShouldBe("feature/200");
        result.Orphan.ShouldBeTrue();
        result.ApexId.ShouldBe(200);
        result.ItemId.ShouldBe(200);
    }

    [Fact]
    public async Task EnsureEvidence_ApexEqualsWorkItem_CollapsesToOrphanForm()
    {
        // Explicit --apex-id matching workItemId → still orphan form. The
        // collapse is by id-equality not by argument absence, so users
        // can't accidentally create both forms for the same item.
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/200", exists: false);
        StubLocalBranchExists(runner, "evidence/200", exists: false);
        StubLsRemote(runner, "feature/200", exists: true);
        StubLocalBranchExists(runner, "feature/200", exists: true);
        StubCreateBranch(runner, "evidence/200", "feature/200");
        StubPush(runner, "evidence/200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: 200));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Branch.ShouldBe("evidence/200");
        result.Orphan.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureEvidence_BaseFeatureMissing_ReturnsRoutingFailure()
    {
        // The apex feature branch must exist on remote before evidence
        // can be created — without it we have nothing to base off.
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: false);
        StubLocalBranchExists(runner, "evidence/100-200", exists: false);
        StubLsRemote(runner, "feature/100", exists: false);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: 100));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Action.ShouldBe("error");
        result.Error!.ShouldContain("ensure-feature");
    }

    [Fact]
    public async Task EnsureEvidence_TargetExistsLocally_JustChecksOutAndPushes()
    {
        // Local branch exists, remote does not → checkout + push to
        // resume an interrupted prior run.
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: false);
        StubLocalBranchExists(runner, "evidence/100-200", exists: true);
        StubCheckout(runner, "evidence/100-200");
        StubPush(runner, "evidence/100-200");
        StubLsRemote(runner, "feature/100", exists: true);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Action.ShouldBe("checked_out");
        result.Pushed.ShouldBeTrue();
        result.RemoteExisted.ShouldBeFalse();
    }

    [Fact]
    public async Task EnsureEvidence_BothLocalAndRemoteExist_ChecksOutWithoutPush()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: true);
        StubLocalBranchExists(runner, "evidence/100-200", exists: true);
        StubCheckout(runner, "evidence/100-200");
        StubLsRemote(runner, "feature/100", exists: true);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Action.ShouldBe("checked_out");
        result.Pushed.ShouldBeFalse();
        result.RemoteExisted.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureEvidence_RemoteOnly_FetchesAndCreatesTracking()
    {
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: true);
        StubLocalBranchExists(runner, "evidence/100-200", exists: false);
        StubFetch(runner, "evidence/100-200");
        StubCheckoutTracking(runner, "evidence/100-200");
        StubLsRemote(runner, "feature/100", exists: true);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Action.ShouldBe("checked_out");
        result.Pushed.ShouldBeFalse();
        result.RemoteExisted.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureEvidence_BaseFeatureRemoteOnly_FetchesBeforeCreate()
    {
        // Apex feature exists only on remote → must fetch before
        // creating the evidence branch off it.
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: false);
        StubLocalBranchExists(runner, "evidence/100-200", exists: false);
        StubLsRemote(runner, "feature/100", exists: true);
        StubLocalBranchExists(runner, "feature/100", exists: false);
        StubFetch(runner, "feature/100");
        StubCheckoutTracking(runner, "feature/100");
        StubCreateBranch(runner, "evidence/100-200", "feature/100");
        StubPush(runner, "evidence/100-200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: 100));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.BaseFetched.ShouldBeTrue();
        result.BaseRemoteExisted.ShouldBeTrue();
        result.Action.ShouldBe("created");
    }

    [Fact]
    public async Task EnsureEvidence_CustomFromRef_OverridesDefaultFeatureBase()
    {
        // --from-ref points at an arbitrary base (e.g. an MG branch for
        // layered evidence on top of a merge-group's worktree).
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: false);
        StubLocalBranchExists(runner, "evidence/100-200", exists: false);
        StubLsRemote(runner, "mg/100_core", exists: true);
        StubLocalBranchExists(runner, "mg/100_core", exists: true);
        StubCreateBranch(runner, "evidence/100-200", "mg/100_core");
        StubPush(runner, "evidence/100-200");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: 100, fromRef: "mg/100_core"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.BaseBranch.ShouldBe("mg/100_core");
        result.CreatedFrom.ShouldBe("mg/100_core");
        result.FromRef.ShouldBe("mg/100_core");
    }

    [Fact]
    public async Task EnsureEvidence_CustomFromRefMissing_ReturnsRoutingFailure()
    {
        // Custom --from-ref must exist on remote; the error message
        // points the operator at the from-ref, not at ensure-feature
        // (since they explicitly chose a non-default base).
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: false);
        StubLocalBranchExists(runner, "evidence/100-200", exists: false);
        StubLsRemote(runner, "mg/100_does-not-exist", exists: false);

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(
                workItemId: 200, apexId: 100, fromRef: "mg/100_does-not-exist"));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Error!.ShouldContain("--from-ref");
    }

    [Fact]
    public async Task EnsureEvidence_GitFailure_ReturnsCacheError()
    {
        // Generic git non-zero (push refused, repo lock, etc.) maps to
        // CacheError; the message bubbles up so the workflow can route.
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: false);
        StubLocalBranchExists(runner, "evidence/100-200", exists: false);
        StubLsRemote(runner, "feature/100", exists: true);
        StubLocalBranchExists(runner, "feature/100", exists: true);
        StubCreateBranch(runner, "evidence/100-200", "feature/100");
        runner.WhenExact("git", ["push", "-u", "origin", "evidence/100-200"],
            new ProcessResult(1, "", "fatal: remote rejected push"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: 100));
        exit.ShouldBe(ExitCodes.CacheError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Action.ShouldBe("error");
        result.Error!.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task EnsureEvidence_JsonContract_PreservesSnakeCaseKeys()
    {
        // Wire-format pin: workflow YAML and downstream tools parse this
        // JSON. Any key rename must be a deliberate, coordinated change.
        var (cmd, runner) = CreateCommand();
        StubLsRemote(runner, "evidence/100-200", exists: true);
        StubLocalBranchExists(runner, "evidence/100-200", exists: true);
        StubCheckout(runner, "evidence/100-200");
        StubLsRemote(runner, "feature/100", exists: true);

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 200, apexId: 100));

        output.ShouldContain("\"branch\"");
        output.ShouldContain("\"base_branch\"");
        output.ShouldContain("\"action\"");
        output.ShouldContain("\"remote_existed\"");
        output.ShouldContain("\"pushed\"");
        output.ShouldContain("\"base_remote_existed\"");
        output.ShouldContain("\"base_fetched\"");
        output.ShouldContain("\"apex_id\"");
        output.ShouldContain("\"item_id\"");
        output.ShouldContain("\"orphan\"");
        output.ShouldContain("\"from_ref\"");
    }

    [Fact]
    public async Task EnsureEvidence_ErrorEnvelope_PreservesFieldsAndApexResolution()
    {
        // Error path still produces a well-formed envelope; importantly,
        // omitted apex resolves to workItemId in the error output too,
        // so callers can inspect ApexId without checking Action first.
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.EnsureEvidenceBranch(workItemId: 0));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.BranchEnsureEvidenceResult)!;
        result.Action.ShouldBe("error");
        result.ItemId.ShouldBe(0);
        result.ApexId.ShouldBe(0);
        result.Branch.ShouldBe("");
        result.BaseBranch.ShouldBe("");
        result.Error!.ShouldNotBeEmpty();
    }
}
