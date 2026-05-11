using Polyphony.Infrastructure.Worktrees;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Worktrees;

/// <summary>
/// Path resolution is layout-aware (bare vs non-bare gitdirs converge on
/// the same sibling runs root). Test inputs use <see cref="Path.Combine"/>
/// so the assertions stay platform-agnostic — Windows builds canonicalize
/// to <c>C:\…\polyphony-runs</c>, *nix to <c>/…/polyphony-runs</c>.
/// </summary>
public sealed class RunsRootResolverTests
{
    private static string Abs(params string[] segments) =>
        Path.GetFullPath(Path.Combine([Path.GetTempPath(), .. segments]));

    [Fact]
    public void Resolve_BareLayout_RunsRootIsSiblingNamedRepoMinusGitPlusRuns()
    {
        var commonDir = Abs("projects", "polyphony.git");

        var (runsRoot, mainWorktree) = RunsRootResolver.Resolve(commonDir);

        runsRoot.ShouldBe(Abs("projects", "polyphony-runs"));
        mainWorktree.ShouldBe(Abs("projects", "polyphony"));
    }

    [Fact]
    public void Resolve_NonBareLayout_RunsRootIsSiblingOfRepoDir()
    {
        var commonDir = Abs("projects", "polyphony", ".git");

        var (runsRoot, mainWorktree) = RunsRootResolver.Resolve(commonDir);

        runsRoot.ShouldBe(Abs("projects", "polyphony-runs"));
        mainWorktree.ShouldBe(Abs("projects", "polyphony"));
    }

    [Fact]
    public void Resolve_BareAndNonBare_ResolveToSameRunsRoot()
    {
        // The convergence guarantee: operators may run mid-migration with
        // either layout; verbs must agree on the runs root either way.
        var bare = RunsRootResolver.Resolve(Abs("projects", "polyphony.git"));
        var nonBare = RunsRootResolver.Resolve(Abs("projects", "polyphony", ".git"));

        bare.RunsRoot.ShouldBe(nonBare.RunsRoot);
        bare.MainWorktreePath.ShouldBe(nonBare.MainWorktreePath);
    }

    [Fact]
    public void Resolve_TrailingSeparator_NormalizedAway()
    {
        var withTrailing = Abs("projects", "polyphony.git") + Path.DirectorySeparatorChar;

        var (runsRoot, mainWorktree) = RunsRootResolver.Resolve(withTrailing);

        runsRoot.ShouldBe(Abs("projects", "polyphony-runs"));
        mainWorktree.ShouldBe(Abs("projects", "polyphony"));
    }

    [Fact]
    public void Resolve_NestedRepoBasename_PreservesBasename()
    {
        // Edge: operator names the repo with a hyphen; the slug should
        // round-trip into the runs-root name without surprises.
        var commonDir = Abs("teams", "platform", "my-cool-repo.git");

        var (runsRoot, mainWorktree) = RunsRootResolver.Resolve(commonDir);

        runsRoot.ShouldBe(Abs("teams", "platform", "my-cool-repo-runs"));
        mainWorktree.ShouldBe(Abs("teams", "platform", "my-cool-repo"));
    }

    [Fact]
    public void Resolve_EmptyOrNull_Throws()
    {
        Should.Throw<ArgumentException>(() => RunsRootResolver.Resolve(""));
        Should.Throw<ArgumentException>(() => RunsRootResolver.Resolve(null!));
    }

    [Fact]
    public void Resolve_UnusualBasename_TreatsAsRepoDir()
    {
        // No `.git` suffix at all: treat the whole path as the repo dir
        // and synthesize a sibling runs root. Keeps the verbs functional
        // for operators with non-standard layouts (e.g. submodule gitdirs).
        var commonDir = Abs("custom", "weird-gitdir");

        var (runsRoot, mainWorktree) = RunsRootResolver.Resolve(commonDir);

        runsRoot.ShouldBe(Abs("custom", "weird-gitdir-runs"));
        mainWorktree.ShouldBe(Abs("custom", "weird-gitdir"));
    }
}
