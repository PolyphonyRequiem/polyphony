using Polyphony.Infrastructure.Worktrees;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Worktrees;

/// <summary>
/// Path-boundary semantics: rejects naive prefix collisions
/// (<c>polyphony</c> vs <c>polyphony-runs</c>), accepts proper
/// descendants, accepts equality, normalizes trailing separators.
/// </summary>
public sealed class PathBoundaryTests
{
    private static string Tmp(params string[] parts)
        => Path.Combine(new[] { Path.GetTempPath() }.Concat(parts).ToArray());

    [Fact]
    public void IsSameOrSubpath_EqualPaths_ReturnsTrue()
    {
        var p = Tmp("foo");
        PathBoundary.IsSameOrSubpath(p, p).ShouldBeTrue();
    }

    [Fact]
    public void IsSameOrSubpath_TrailingSeparatorOnParent_StillEqual()
    {
        var parent = Tmp("foo") + Path.DirectorySeparatorChar;
        var child = Tmp("foo");
        PathBoundary.IsSameOrSubpath(parent, child).ShouldBeTrue();
    }

    [Fact]
    public void IsSameOrSubpath_DirectChild_ReturnsTrue()
    {
        var parent = Tmp("polyphony-runs");
        var child = Tmp("polyphony-runs", "apex-3085");
        PathBoundary.IsSameOrSubpath(parent, child).ShouldBeTrue();
    }

    [Fact]
    public void IsSameOrSubpath_NestedChild_ReturnsTrue()
    {
        var parent = Tmp("polyphony-runs");
        var child = Tmp("polyphony-runs", "apex-3085", "feature-3085");
        PathBoundary.IsSameOrSubpath(parent, child).ShouldBeTrue();
    }

    [Fact]
    public void IsSameOrSubpath_RejectsNaivePrefixCollision()
    {
        // The headline reason this helper exists. polyphony-runs is a
        // sibling of polyphony in the AB#3085 layout, NOT a descendant.
        var parent = Tmp("polyphony");
        var child = Tmp("polyphony-runs", "apex-3085", "feature-3085");
        PathBoundary.IsSameOrSubpath(parent, child).ShouldBeFalse();
    }

    [Fact]
    public void IsSameOrSubpath_UnrelatedPaths_ReturnsFalse()
    {
        var parent = Tmp("foo");
        var child = Tmp("bar", "baz");
        PathBoundary.IsSameOrSubpath(parent, child).ShouldBeFalse();
    }

    [Fact]
    public void IsSameOrSubpath_ParentDeeperThanChild_ReturnsFalse()
    {
        var parent = Tmp("a", "b", "c");
        var child = Tmp("a", "b");
        PathBoundary.IsSameOrSubpath(parent, child).ShouldBeFalse();
    }

    [Fact]
    public void IsSameOrSubpath_DotDotInChild_NormalizedBeforeCompare()
    {
        var parent = Tmp("foo");
        // foo/bar/.. resolves to foo — equal.
        var child = Tmp("foo", "bar", "..");
        PathBoundary.IsSameOrSubpath(parent, child).ShouldBeTrue();
    }

    [Fact]
    public void IsSameOrSubpath_DotDotEscapesParent_ReturnsFalse()
    {
        var parent = Tmp("foo", "bar");
        var child = Tmp("foo", "bar", "..", "baz");
        PathBoundary.IsSameOrSubpath(parent, child).ShouldBeFalse();
    }

    [Fact]
    public void IsSameOrSubpath_NullParent_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            PathBoundary.IsSameOrSubpath(null!, Tmp("x")));
    }

    [Fact]
    public void IsSameOrSubpath_EmptyChild_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            PathBoundary.IsSameOrSubpath(Tmp("x"), ""));
    }

    [Fact]
    public void IsSameOrSubpath_WindowsCaseInsensitive()
    {
        if (!OperatingSystem.IsWindows()) return;

        var parent = Tmp("Foo");
        var child = Tmp("foo", "bar");
        PathBoundary.IsSameOrSubpath(parent, child).ShouldBeTrue();
    }
}
