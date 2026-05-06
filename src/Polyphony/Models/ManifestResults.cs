using Polyphony.Manifest;

namespace Polyphony;

/// <summary>JSON output for <c>polyphony manifest read</c>.</summary>
public sealed record ManifestReadResult
{
    /// <summary>Path the manifest was read from.</summary>
    public required string Path { get; init; }

    /// <summary>The full manifest contents (snake-case via JsonContext).</summary>
    public required RunManifest Manifest { get; init; }

    /// <summary>The freshly-computed topology hash for the loaded merge groups.</summary>
    public required string ComputedTopologyHash { get; init; }

    /// <summary>True iff the stored hash matches the computed hash.</summary>
    public required bool TopologyHashMatches { get; init; }

    /// <summary>Validation error when read fails.</summary>
    public string? Error { get; init; }
}

/// <summary>JSON output for <c>polyphony manifest topology-hash</c>.</summary>
public sealed record ManifestTopologyHashResult
{
    /// <summary>Path the manifest was read from.</summary>
    public required string Path { get; init; }

    /// <summary>The freshly-computed topology hash.</summary>
    public required string TopologyHash { get; init; }

    /// <summary>The hash currently stored in the manifest file.</summary>
    public required string StoredTopologyHash { get; init; }

    /// <summary>True iff the stored hash matches the freshly-computed hash.</summary>
    public required bool Matches { get; init; }

    /// <summary>Validation error when computation fails.</summary>
    public string? Error { get; init; }
}

/// <summary>JSON output for <c>polyphony manifest record-rebase</c>.</summary>
public sealed record ManifestRebaseRecordResult
{
    /// <summary>Path the manifest was modified at.</summary>
    public required string Path { get; init; }

    /// <summary>The total number of recorded rebases after this append.</summary>
    public required int RebaseCount { get; init; }

    /// <summary>The branch the rebase targeted.</summary>
    public required string Branch { get; init; }

    /// <summary>The branch the rebase landed onto.</summary>
    public required string Onto { get; init; }

    /// <summary>The recorded reason category.</summary>
    public required string Reason { get; init; }

    /// <summary>The post-rebase HEAD commit.</summary>
    public required string Commit { get; init; }

    /// <summary>UTC timestamp recorded with the rebase.</summary>
    public required DateTime RecordedAt { get; init; }

    /// <summary>Validation error when the operation fails.</summary>
    public string? Error { get; init; }
}

/// <summary>JSON output for <c>polyphony manifest record-approval</c>.</summary>
public sealed record ManifestApprovalRecordResult
{
    /// <summary>Path the manifest was modified at.</summary>
    public required string Path { get; init; }

    /// <summary>The total number of recorded approvals after this append.</summary>
    public required int ApprovalCount { get; init; }

    /// <summary>The named gate that was approved.</summary>
    public required string Gate { get; init; }

    /// <summary>The approver's display name.</summary>
    public required string ApprovedBy { get; init; }

    /// <summary>UTC timestamp recorded with the approval.</summary>
    public required DateTime ApprovedAt { get; init; }

    /// <summary>Free-form detail.</summary>
    public string? Detail { get; init; }

    /// <summary>Validation error when the operation fails.</summary>
    public string? Error { get; init; }
}
