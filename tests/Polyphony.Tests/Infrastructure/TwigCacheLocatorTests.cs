using Polyphony.Infrastructure;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure;

/// <summary>
/// Tests for <see cref="TwigCacheLocator.ResolveTwigDir"/> covering explicit path resolution,
/// walk-up discovery, and error cases.
/// Uses real temporary directories to exercise actual filesystem traversal.
/// </summary>
public sealed class TwigCacheLocatorTests : IDisposable
{
    private readonly string _tempRoot;

    public TwigCacheLocatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"twig-locator-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    // ──────────── Explicit path: IS .twig/ directory ────────────

    [Fact]
    public void ResolveTwigDir_ExplicitPathIsTwigDir_ReturnsPath()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);

        var result = TwigCacheLocator.ResolveTwigDir(twigDir);

        result.ShouldBe(twigDir);
    }

    // ──────────── Explicit path: CONTAINS .twig/ subdirectory ────────────

    [Fact]
    public void ResolveTwigDir_ExplicitPathContainsTwigSubDir_ReturnsTwigSubDir()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);

        var result = TwigCacheLocator.ResolveTwigDir(_tempRoot);

        result.ShouldBe(twigDir);
    }

    // ──────────── Explicit path: does not exist ────────────

    [Fact]
    public void ResolveTwigDir_ExplicitPathDoesNotExist_ThrowsDirectoryNotFound()
    {
        var badPath = Path.Combine(_tempRoot, "nonexistent");

        var ex = Should.Throw<DirectoryNotFoundException>(
            () => TwigCacheLocator.ResolveTwigDir(badPath));

        ex.Message.ShouldContain("does not exist");
    }

    // ──────────── Explicit path: exists but has no .twig/ ────────────

    [Fact]
    public void ResolveTwigDir_ExplicitPathExistsButNoTwig_ThrowsDirectoryNotFound()
    {
        var emptyDir = Path.Combine(_tempRoot, "project");
        Directory.CreateDirectory(emptyDir);

        var ex = Should.Throw<DirectoryNotFoundException>(
            () => TwigCacheLocator.ResolveTwigDir(emptyDir));

        ex.Message.ShouldContain(".twig/");
    }

    // ──────────── Walk-up: finds .twig/ in start directory ────────────

    [Fact]
    public void ResolveTwigDir_NullPath_FindsTwigInStartDir()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);

        var result = TwigCacheLocator.ResolveTwigDir(null, startDir: _tempRoot);

        result.ShouldBe(twigDir);
    }

    // ──────────── Walk-up: finds .twig/ in ancestor directory ────────────

    [Fact]
    public void ResolveTwigDir_NullPath_FindsTwigInAncestor()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);

        var child = Path.Combine(_tempRoot, "src", "MyProject");
        Directory.CreateDirectory(child);

        var result = TwigCacheLocator.ResolveTwigDir(null, startDir: child);

        result.ShouldBe(twigDir);
    }

    // ──────────── Walk-up: deeply nested ────────────

    [Fact]
    public void ResolveTwigDir_DeeplyNested_WalksUpMultipleLevels()
    {
        var twigDir = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(twigDir);

        var deep = Path.Combine(_tempRoot, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(deep);

        var result = TwigCacheLocator.ResolveTwigDir(null, startDir: deep);

        result.ShouldBe(twigDir);
    }

    // ──────────── Walk-up: .twig/ not found anywhere ────────────

    [Fact]
    public void ResolveTwigDir_NoTwigAnywhere_ThrowsWithHelpfulMessage()
    {
        // Use a path near the drive root to avoid finding .twig/ from the
        // test runner's own repo during walk-up.
        var testRoot = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, $"twig-test-nf-{Guid.NewGuid():N}")
            : Path.Combine(Path.GetTempPath(), $"twig-test-nf-{Guid.NewGuid():N}");
        var deep = Path.Combine(testRoot, "a", "b");
        Directory.CreateDirectory(deep);

        try
        {
            var ex = Should.Throw<DirectoryNotFoundException>(
                () => TwigCacheLocator.ResolveTwigDir(null, startDir: deep));

            ex.Message.ShouldContain("--twig-dir");
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    // ──────────── Walk-up: nearest .twig/ wins ────────────

    [Fact]
    public void ResolveTwigDir_MultipleTwigDirs_ReturnsNearest()
    {
        // Outer .twig/
        var outerTwig = Path.Combine(_tempRoot, ".twig");
        Directory.CreateDirectory(outerTwig);

        // Inner .twig/ (closer to start)
        var innerDir = Path.Combine(_tempRoot, "nested");
        var innerTwig = Path.Combine(innerDir, ".twig");
        Directory.CreateDirectory(innerTwig);

        var child = Path.Combine(innerDir, "src");
        Directory.CreateDirectory(child);

        var result = TwigCacheLocator.ResolveTwigDir(null, startDir: child);

        result.ShouldBe(innerTwig);
    }
}
