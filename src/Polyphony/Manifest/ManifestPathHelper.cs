using Polyphony.Annotations;
using Polyphony.Infrastructure.Paths;

namespace Polyphony.Manifest;

/// <summary>
/// Internal helper used by manifest-reading verbs in <c>PlanCommands</c>
/// and <c>PrCommands</c> to resolve the on-disk manifest path from the
/// Rev 4.2 <c>--root-id</c>/<c>--path</c> decision matrix.
/// </summary>
/// <remarks>
/// Mirrors the resolution rules in <c>ManifestCommands.cs</c> but
/// surfaces a flat tuple instead of the path-source envelope (the
/// reading verbs don't need to report <c>path_source</c> in their
/// output). Never throws on git/path failures — converts to a populated
/// <see cref="ResolvedManifestPath.Error"/> the caller renders into its
/// own error envelope.
///
/// <para>Unlike <c>ManifestCommands</c>, missing-and-invalid root id is
/// a hard failure here: the consumers (open-plan-pr, merge-plan-pr,
/// detect-state, etc.) all carry <c>--root-id</c> through their own
/// required-input checks before calling this. The legacy
/// <c>.polyphony/run.yaml</c> fallback is gone — the local manifest
/// lives under <c>&lt;git-common-dir&gt;/polyphony/&lt;rootId&gt;/</c> by
/// construction.</para>
/// </remarks>
internal readonly record struct ResolvedManifestPath(
    string Path,
    string? Error);

internal static class ManifestPathHelper
{
    /// <summary>
    /// Resolves the local manifest path from <paramref name="explicitPath"/>
    /// and <paramref name="rootId"/>. When <paramref name="explicitPath"/>
    /// is non-empty, returns it verbatim (the testing seam). Otherwise
    /// derives via <see cref="PolyphonyStatePaths.GetManifestPathAsync"/>.
    /// </summary>
    public static async Task<ResolvedManifestPath> ResolveAsync(
        PolyphonyStatePaths statePaths,
        int rootId,
        string explicitPath,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return new ResolvedManifestPath(explicitPath, Error: null);
        }

        if (rootId == RequiredInput.MissingInt || rootId <= 0)
        {
            return new ResolvedManifestPath(
                Path: string.Empty,
                Error: $"manifest path resolution requires --root-id (got {rootId}); pass --path explicitly to override.");
        }

        try
        {
            var derived = await statePaths.GetManifestPathAsync(rootId, ct).ConfigureAwait(false);
            return new ResolvedManifestPath(derived, Error: null);
        }
        catch (InvalidOperationException ex)
        {
            return new ResolvedManifestPath(string.Empty, Error: ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new ResolvedManifestPath(string.Empty, Error: ex.Message);
        }
    }
}
