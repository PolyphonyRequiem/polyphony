using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class ResetCommandsBranchesEnumerationTests : CommandTestBase
{
    private (ResetCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var pullRequestReader = new PullRequestReader(gh, null);
        var resolver = new RepoIdentityResolver(git);
        var planObserver = new PlanObserver(git, gh, new ThrowingAdoClient(), twig, resolver);
        var walker = new HierarchyWalker(Config, Repository);

        return (new ResetCommands(twig, git, pullRequestReader, planObserver, walker), runner);
    }

    private static void StubBranchEnumeration(FakeProcessRunner runner)
    {
        var branchesByPattern = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["refs/heads/plan/100"] = ["plan/100"],
            ["refs/heads/plan/100-*"] = ["plan/100-101", "plan/100-102"],
            ["refs/heads/sdlc/apex/100"] = ["sdlc/apex/100"],
            ["refs/heads/sdlc/apex/101"] = ["sdlc/apex/101"],
            ["refs/heads/sdlc/apex/102"] = ["sdlc/apex/102"],
            ["refs/heads/sdlc/apex/103"] = ["sdlc/apex/103"],
            ["refs/heads/sdlc/apex/999"] = ["sdlc/apex/999"],
        };

        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 4 && a[0] == "ls-remote" && a[1] == "--heads",
            (a, _) =>
            {
                var pattern = a[3];
                var stdout = branchesByPattern.TryGetValue(pattern, out var branches)
                    ? string.Join("\n", branches.Select(branch => $"sha\trefs/heads/{branch}")) + "\n"
                    : string.Empty;
                return Task.FromResult(new ProcessResult(0, stdout, string.Empty));
            });

        runner.WhenAsync(
            (e, a) => e == "git" && a.Count >= 1 && a[0] == "for-each-ref",
            (_, _) => Task.FromResult(new ProcessResult(0, string.Empty, string.Empty)));
    }

    private async Task SeedHierarchyAsync()
    {
        var apex = new WorkItemBuilder()
            .WithId(100)
            .WithType("Epic")
            .WithTitle("Apex")
            .WithState("Doing")
            .Build();
        var child101 = new WorkItemBuilder()
            .WithId(101)
            .WithType("Issue")
            .WithTitle("Child 101")
            .WithState("To Do")
            .WithParentId(100)
            .Build();
        var child102 = new WorkItemBuilder()
            .WithId(102)
            .WithType("Issue")
            .WithTitle("Child 102")
            .WithState("To Do")
            .WithParentId(100)
            .Build();
        var descendant103 = new WorkItemBuilder()
            .WithId(103)
            .WithType("Task")
            .WithTitle("Descendant 103")
            .WithState("To Do")
            .WithParentId(101)
            .Build();

        await SeedAsync(apex, child101, child102, descendant103);
    }

    [Fact]
    public async Task ResetBranches_DryRun_EnumeratesNestedPlanAndDescendantSdlcBranches()
    {
        await SeedHierarchyAsync();
        var (cmd, runner) = CreateCommand();
        StubBranchEnumeration(runner);

        var (exitCode, output) = await CaptureConsoleAsync(() => cmd.ResetBranches(apex: 100, execute: false));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResetBranchesResult);
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.DryRun.ShouldBeTrue();

        var branches = result.DeletedBranches.Select(x => x.Branch).ToList();
        branches.ShouldContain("plan/100");
        branches.ShouldContain("plan/100-101");
        branches.ShouldContain("plan/100-102");
        branches.ShouldContain("sdlc/apex/100");
        branches.ShouldContain("sdlc/apex/101");
        branches.ShouldContain("sdlc/apex/102");
        branches.ShouldContain("sdlc/apex/103");
        branches.ShouldNotContain("sdlc/apex/999");

        runner.Invocations.ShouldContain(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 4
            && i.Arguments[0] == "ls-remote"
            && i.Arguments[3] == "refs/heads/sdlc/apex/103");
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 4
            && i.Arguments[0] == "ls-remote"
            && i.Arguments[3] == "refs/heads/sdlc/apex/999");
    }

    [Fact]
    public async Task EnumerateApexBranchesAsync_IncludesOnlyApexAndDescendantSdlcBranches()
    {
        await SeedHierarchyAsync();
        var (cmd, runner) = CreateCommand();
        StubBranchEnumeration(runner);

        var branches = await cmd.EnumerateApexBranchesAsync(100, CancellationToken.None);

        branches.ShouldContain("plan/100-101");
        branches.ShouldContain("plan/100-102");
        branches.ShouldContain("sdlc/apex/100");
        branches.ShouldContain("sdlc/apex/101");
        branches.ShouldContain("sdlc/apex/102");
        branches.ShouldContain("sdlc/apex/103");
        branches.ShouldNotContain("sdlc/apex/999");

        runner.Invocations.ShouldContain(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 4
            && i.Arguments[0] == "ls-remote"
            && i.Arguments[3] == "refs/heads/sdlc/apex/103");
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "git"
            && i.Arguments.Count >= 4
            && i.Arguments[0] == "ls-remote"
            && i.Arguments[3] == "refs/heads/sdlc/apex/999");
    }
}
