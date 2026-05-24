using NSubstitute;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal.Observers;

public sealed class GitBranchObserverDiscoveredResourceTests
{
    private const int Root = 1234;

    [Fact]
    public async Task ObserveAsync_includes_sdlc_root_orphan_in_discovered_resources()
    {
        var git = StubGitClient(
            localBranches: ["main", "feature/1234", $"sdlc/root/{Root}"],
            remoteBranches: ["main", "feature/1234"]);

        var observer = new GitBranchObserver(git);
        var batch = await observer.ObserveAsync(
            new ResourceObservationRequest { RootId = Root, ExpectedResources = [] },
            CancellationToken.None);

        var ids = batch.DiscoveredResources.Select(d => d.Id).ToArray();
        ids.ShouldContain("feature/1234");
        ids.ShouldContain($"sdlc/root/{Root}");
        ids.ShouldNotContain("main");
        batch.DiscoveredResources.ShouldAllBe(d => d.MatchesPolyphonyPattern);
    }

    [Fact]
    public async Task ObserveAsync_excludes_other_roots_sdlc_root_branch()
    {
        var git = StubGitClient(
            localBranches: ["main", "sdlc/root/9999"],
            remoteBranches: []);

        var observer = new GitBranchObserver(git);
        var batch = await observer.ObserveAsync(
            new ResourceObservationRequest { RootId = Root, ExpectedResources = [] },
            CancellationToken.None);

        batch.DiscoveredResources.Select(d => d.Id).ShouldNotContain("sdlc/root/9999");
    }

    [Fact]
    public async Task ObserveAsync_does_not_duplicate_expected_sdlc_root_into_discovered()
    {
        var git = StubGitClient(
            localBranches: [$"sdlc/root/{Root}"],
            remoteBranches: []);
        var observer = new GitBranchObserver(git);

        var expected = new ProjectedResourceState
        {
            Key = new ResourceKey { Kind = ResourceKind.GitBranch, Id = $"sdlc/root/{Root}" },
            Action = "test_setup",
            StartedAt = 0,
            Intent = ResourceIntent.EnsurePresent,
            Mutation = ResourceMutation.CreatedNow,
            PolyphonyOwned = true,
        };
        var batch = await observer.ObserveAsync(
            new ResourceObservationRequest { RootId = Root, ExpectedResources = [expected] },
            CancellationToken.None);

        batch.DiscoveredResources.ShouldBeEmpty();
        batch.Observations.Single().Id.ShouldBe($"sdlc/root/{Root}");
    }

    private static IGitClient StubGitClient(IReadOnlyList<string> localBranches, IReadOnlyList<string> remoteBranches)
    {
        var git = Substitute.For<IGitClient>();
        git.ListLocalBranchesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(localBranches);
        git.ListRemoteBranchesAsync(Arg.Any<CancellationToken>()).Returns(remoteBranches);

        git.RevParseLocalBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var branch = ci.ArgAt<string>(0);
                return Task.FromResult<string?>(localBranches.Contains(branch) ? "deadbeef" : null);
            });

        git.LsRemoteHeadsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                const string prefix = "refs/heads/";
                var pattern = ci.ArgAt<string>(1);
                var branch = pattern.StartsWith(prefix, StringComparison.Ordinal) ? pattern[prefix.Length..] : pattern;
                IReadOnlyList<string> heads = remoteBranches.Contains(branch) ? [$"cafef00d\t{branch}"] : [];
                return Task.FromResult(heads);
            });

        return git;
    }
}

