namespace Polyphony;

/// <summary>
/// Aggregated dashboard snapshot for a single apex work item. Composed by
/// <c>polyphony status</c> from the ADO cache, the run manifest, and a
/// best-effort gh PR query. Always exit 0 (routing-style); failure modes
/// are surfaced via the <see cref="Warnings"/> array and per-section
/// <c>error</c> fields rather than via process exit code.
///
/// <para>Designed for periodic polling (e.g. a dashboard widget). Cross-signal
/// detections live in <see cref="Warnings"/>; the <see cref="Headline"/>
/// + <see cref="NextAction"/> pair give a one-line human-readable summary.</para>
/// </summary>
public sealed record StatusResult
{
    public required int ApexId { get; init; }
    public required StatusAdoSection Ado { get; init; }
    public required StatusManifestSection Manifest { get; init; }
    public required StatusFeaturePrSection FeaturePr { get; init; }
    public required StatusBinarySection Binary { get; init; }
    public required IReadOnlyList<StatusWarning> Warnings { get; init; }
    public required string Headline { get; init; }
    public string? NextAction { get; init; }
}

/// <summary>
/// ADO-side observable signals: type, state, title, raw tags, plus
/// derived booleans for the polyphony tag namespace and the count of
/// direct children. <see cref="Error"/> is populated when the lookup
/// failed (e.g. work item not in cache); the rest of the section is
/// best-effort empty in that case so consumers can still render.
/// </summary>
public sealed record StatusAdoSection
{
    public required bool Found { get; init; }
    public string? Type { get; init; }
    public string? State { get; init; }
    public string? Title { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required bool InScope { get; init; }
    public required bool IsRoot { get; init; }
    public required bool HasPlannedTag { get; init; }
    public required int ChildrenCount { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Run-manifest snapshot. <see cref="Exists"/> false means the file is
/// absent (not yet initialised). When present, the rest of the section
/// reflects the parsed manifest. <see cref="Error"/> is populated when
/// the file existed but failed to parse.
/// </summary>
public sealed record StatusManifestSection
{
    public required bool Exists { get; init; }
    public string? Path { get; init; }
    public string? FeatureBranch { get; init; }
    public int? PlanGenerationsRoot { get; init; }
    public int? MergedPlanPrsCount { get; init; }
    public int? MergeGroupsCount { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Feature PR (head <c>feature/{apex_id}</c> → main) summary. The lookup
/// is gh-best-effort: a missing PR yields <see cref="Exists"/> false, a
/// transient gh failure yields <see cref="Error"/> populated and the rest
/// of the section empty.
/// </summary>
public sealed record StatusFeaturePrSection
{
    public required bool Exists { get; init; }
    public int? Number { get; init; }
    public string? Url { get; init; }
    public string? State { get; init; }
    public string? MergedAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Self-reported polyphony binary metadata. <see cref="InformationalVersion"/>
/// is the SemVer-with-buildmetadata that MinVer writes; <see cref="Version"/>
/// is the numeric AssemblyVersion (stable across pre-releases). Both are
/// surfaced because operators frequently want to spot-check that a worktree
/// is running the binary they think it is (the AB#3064 dogfood was bitten by
/// a stale binary).
/// </summary>
public sealed record StatusBinarySection
{
    public required string Version { get; init; }
    public required string InformationalVersion { get; init; }
    public string? Location { get; init; }
}

/// <summary>
/// Cross-signal detection. Codes are stable identifiers safe for routing
/// (<c>planned_tag_zero_children</c>, <c>apex_not_in_scope</c>, etc.);
/// <see cref="Message"/> is human-readable and may be surfaced verbatim
/// in dashboards.
/// </summary>
public sealed record StatusWarning
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}
