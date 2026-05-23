namespace Polyphony.Models;

/// <summary>
/// W15 (AB#3285): result envelope for the
/// <c>polyphony lineage status --root</c> diagnostic. Pure read-only
/// JSON; carries no side-effects. Designed for:
/// <list type="bullet">
///   <item>Operators triaging a stuck run ("which lineage am I in?
///   does the journal still know about me?").</item>
///   <item>The forthcoming <c>polyphony reconcile</c> verb (W13)
///   as a pre-flight check before any superseding observations are
///   appended.</item>
///   <item>The cross-machine attach UX (W14) — same data, used to
///   refuse parallel-lineage minting.</item>
/// </list>
/// </summary>
public sealed record LineageStatusResult
{
    /// <summary>Root work-item ID the report was scoped to.</summary>
    public required int Root { get; init; }

    /// <summary>
    /// Current run id as resolved from <see cref="Polyphony.Configuration.RunContext"/>
    /// at verb invocation. Carries a <c>manual_*</c> sentinel when no
    /// <c>POLYPHONY_RUN_ID</c> was exported by the launcher.
    /// </summary>
    public required string CurrentRunId { get; init; }

    /// <summary>
    /// True when <see cref="CurrentRunId"/> is a <c>manual_*</c>
    /// fallback (no real lineage in play). Diagnostic only — the
    /// verb still reports lineages observed in the journal so the
    /// operator can see which prior lineages exist.
    /// </summary>
    public required bool CurrentLineageIsManual { get; init; }

    /// <summary>
    /// Path to the resolved run manifest, or <c>null</c> when no
    /// manifest exists yet. When non-null but unreadable, see
    /// <see cref="ManifestError"/>.
    /// </summary>
    public string? ManifestPath { get; init; }

    /// <summary>
    /// <see cref="Polyphony.Manifest.RunManifest.RunId"/> as recorded
    /// in the on-disk run manifest (W2). Mismatch with
    /// <see cref="CurrentRunId"/> is a partition signal.
    /// </summary>
    public string? ManifestRunId { get; init; }

    /// <summary>Set when the manifest exists but failed to parse.</summary>
    public string? ManifestError { get; init; }

    /// <summary>
    /// Path of the journal SQLite database the verb consulted.
    /// <c>null</c> for the in-memory <c>NullJournalStore</c>.
    /// </summary>
    public string? JournalPath { get; init; }

    /// <summary>
    /// Every distinct run id observed in the journal for this root.
    /// Stable order (lexicographic) so diff-friendly across runs.
    /// </summary>
    public required IReadOnlyList<LineageObservation> Lineages { get; init; }

    /// <summary>
    /// True when the current run id appears in <see cref="Lineages"/>
    /// — i.e. THIS lineage has at least one journal row for this
    /// root.
    /// </summary>
    public required bool CurrentLineageHasRows { get; init; }

    /// <summary>
    /// True when the journal contains rows for OTHER lineages but
    /// nothing for the current lineage. The classic
    /// "fresh-checkout / wiped-tmpdir / cross-machine" partition
    /// signal — without this, an operator could assume the journal
    /// is clean when in fact it's only this checkout's view that is
    /// blank.
    /// </summary>
    public required bool LooksPartitioned { get; init; }

    /// <summary>
    /// Free-form human-readable headline summarising the verdict.
    /// Stable wording per state so dashboards can match on it.
    /// </summary>
    public required string Verdict { get; init; }
}

/// <summary>
/// One distinct lineage observed in the journal for a given root.
/// </summary>
public sealed record LineageObservation
{
    /// <summary>Distinct <c>run_id</c> column value from the journal.</summary>
    public required string RunId { get; init; }

    /// <summary>Count of <c>actions</c> rows with this run id under this root.</summary>
    public required int RowCount { get; init; }

    /// <summary>
    /// Wall-clock timestamp of the earliest action row for this
    /// lineage under this root (ms-since-epoch). Useful for spotting
    /// "yesterday's stuck run".
    /// </summary>
    public long? FirstSeenAt { get; init; }

    /// <summary>
    /// Wall-clock timestamp of the latest action row (ms-since-epoch).
    /// Combined with <see cref="FirstSeenAt"/> bounds the lineage's
    /// activity window.
    /// </summary>
    public long? LastSeenAt { get; init; }

    /// <summary>
    /// True when this row's run id equals the current run id. Lets
    /// callers highlight the "you are here" row in renderings.
    /// </summary>
    public required bool IsCurrent { get; init; }
}
