using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Commands;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Twig.Domain.Services;
using Xunit;

namespace Polyphony.Tests.Journal;

public sealed class JournalE2ETests : Polyphony.Tests.Commands.CommandTestBase
{
    private readonly string _scratchDir = Path.Combine(AppContext.BaseDirectory, "journal-e2e", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnsureEvidenceBranch_ThenShow_RoundTripsRealJournalEntry()
    {
        Directory.CreateDirectory(_scratchDir);
        var store = new JournalStore(Path.Combine(_scratchDir, ".polyphony-state", "journal.db"));
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var validator = new TransitionValidator(Config);
        var branchCommands = new BranchCommands(
            twig,
            walker,
            Repository,
            validator,
            git,
            Config,
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            new Polyphony.Sdlc.Observers.PullRequestReader(gh, null),
            new RunContext("run-e2e"),
            new JournaledActionDecorator(store));
        var journalCommands = new JournalCommands(store);

        runner.WhenExact("git", ["ls-remote", "--heads", "origin", "evidence/100-200"], new ProcessResult(0, "", ""));
        runner.WhenStartsWithSequence(
            "git",
            ["rev-parse", "--verify", "refs/heads/evidence/100-200"],
            new ProcessResult(1, "", "fatal: needed a single revision"),
            new ProcessResult(0, "abc123\n", ""));
        runner.WhenExact("git", ["ls-remote", "--heads", "origin", "feature/100"], new ProcessResult(0, "base123\trefs/heads/feature/100\n", ""));
        runner.WhenExact("git", ["rev-parse", "--verify", "refs/heads/feature/100"], new ProcessResult(0, "base123\n", ""));
        runner.WhenExact("git", ["checkout", "-b", "evidence/100-200", "feature/100"], new ProcessResult(0, "", ""));
        runner.WhenExact("git", ["push", "-u", "origin", "evidence/100-200"], new ProcessResult(0, "", ""));

        var (ensureExit, _) = await CaptureConsoleAsync(() => branchCommands.EnsureEvidenceBranch(workItemId: 200, rootId: 100));
        var (showExit, showOutput) = await CaptureConsoleAsync(() => journalCommands.Show(action: "branch_ensure_evidence_branch"));

        ensureExit.ShouldBe(ExitCodes.Success);
        showExit.ShouldBe(ExitCodes.Success);

        var showResult = JsonSerializer.Deserialize(showOutput, PolyphonyJsonContext.Default.JournalShowResult);
        showResult.ShouldNotBeNull();
        showResult.Count.ShouldBe(1);
        showResult.Entries[0].Action.ShouldBe("branch_ensure_evidence_branch");
        showResult.Entries[0].Target.ShouldBe("evidence/100-200");
        showResult.Entries[0].Outcome.ShouldBe(JournalOutcome.Success);

        var payload = JsonSerializer.Deserialize(showResult.Entries[0].PayloadJson!, PolyphonyJsonContext.Default.BranchEnsureEvidenceBranchPayload);
        payload.ShouldNotBeNull();
        payload.Sha.ShouldBe("abc123");
    }

    public override void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
