namespace Polyphony.Infrastructure;

/// <summary>
/// Locates the <c>.twig/</c> directory from either an explicit path or by walking up
/// from the current working directory. The resolved directory path is passed to
/// <see cref="Twig.Infrastructure.TwigServiceRegistration.AddTwigCoreServices"/>
/// which handles actual DB path resolution (multi-context layout).
/// </summary>
public static class TwigCacheLocator
{
    private const string TwigDirName = ".twig";

    /// <summary>
    /// Resolves the path to the <c>.twig/</c> directory.
    /// </summary>
    /// <param name="twigDir">Optional explicit path. May point directly to a <c>.twig/</c>
    /// directory or to a parent directory that contains one. When <c>null</c>, walks up
    /// from <paramref name="startDir"/> (or CWD) looking for a <c>.twig/</c> directory.</param>
    /// <param name="startDir">Directory to start the walk-up search from when
    /// <paramref name="twigDir"/> is <c>null</c>. Defaults to
    /// <see cref="Directory.GetCurrentDirectory"/>.</param>
    /// <returns>Full path to the resolved <c>.twig/</c> directory.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// Thrown when the <c>.twig/</c> directory cannot be found.
    /// Callers typically map this to exit code 3.
    /// </exception>
    public static string ResolveTwigDir(string? twigDir, string? startDir = null)
    {
        return twigDir is not null
            ? ResolveExplicit(twigDir)
            : DiscoverFromAncestors(startDir ?? Directory.GetCurrentDirectory());
    }

    private static string ResolveExplicit(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"The specified twig directory '{path}' does not exist.");
        }

        // The path IS the .twig/ directory
        if (IsTwigDirName(fullPath))
            return fullPath;

        // The path CONTAINS a .twig/ subdirectory
        var candidate = Path.Combine(fullPath, TwigDirName);
        if (Directory.Exists(candidate))
            return candidate;

        throw new DirectoryNotFoundException(
            $"The specified path '{path}' does not contain a {TwigDirName}/ directory.");
    }

    private static string DiscoverFromAncestors(string startDir)
    {
        var current = Path.GetFullPath(startDir);

        while (current is not null)
        {
            var candidate = Path.Combine(current, TwigDirName);
            if (Directory.Exists(candidate))
                return candidate;

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate a {TwigDirName}/ directory. " +
            "Searched from the current directory up to the filesystem root. " +
            "Use --twig-dir to specify the path explicitly.");
    }

    private static bool IsTwigDirName(string path) =>
        Path.GetFileName(path).Equals(TwigDirName, StringComparison.OrdinalIgnoreCase);
}
