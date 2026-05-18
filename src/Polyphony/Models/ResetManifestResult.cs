namespace Polyphony;

/// <summary>
/// Output envelope for <c>polyphony reset manifest --apex N</c> — reports
/// on the per-apex run manifest at
/// <c>feature/{N}:.polyphony/run.yaml</c>.
///
/// <para>The manifest is co-located with the feature branch, so deletion
/// is effected by <c>reset branches</c> (which deletes
/// <c>feature/{N}</c>). This verb exists as part of the spec-promised
/// reset family for two reasons:</para>
///
/// <list type="bullet">
///   <item>It surfaces the manifest's state for operator inspection
///         (does a manifest exist? what generation does it claim?).</item>
///   <item>It provides a stable hook for PR 3's reset-apex.yaml workflow
///         and any future need to clear the manifest without nuking the
///         whole feature branch (e.g. partial-reset scenarios).</item>
/// </list>
///
/// <para>Current behavior: read-only inspection. When the apex feature
/// branch + manifest are present, <see cref="ManifestPath"/> and
/// <see cref="ManifestPresent"/> describe what's there. Actual clearing
/// is deferred to <c>reset branches</c>. PR 3 may extend this verb to
/// perform an isolated commit-and-push that removes only the manifest
/// file from the feature branch.</para>
///
/// <para>Routing-style envelope: always exits 0.</para>
/// </summary>
public sealed record ResetManifestResult
{
    public required int Apex { get; init; }
    public required bool Success { get; init; }
    public required bool DryRun { get; init; }

    /// <summary>Conventional branch carrying the manifest — <c>feature/{N}</c>.</summary>
    public required string FeatureBranch { get; init; }

    /// <summary>Conventional manifest path within the feature branch.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>True when the feature branch exists on origin.</summary>
    public bool FeatureBranchExists { get; init; }

    /// <summary>True when a <c>.polyphony/run.yaml</c> blob exists at the tip of <see cref="FeatureBranch"/>.</summary>
    public bool ManifestPresent { get; init; }

    /// <summary>
    /// Always emitted: a hint about which other reset verb (or workflow
    /// step) will actually clear the manifest. Read-only verb today.
    /// </summary>
    public string DeferralReason { get; init; } = string.Empty;

    public string? Error { get; init; }
}
