namespace Polyphony;

/// <summary>
/// Output of <c>polyphony state detect</c>: the root SDLC's authoritative
/// view of the work item's lifecycle position. Mirrors the JSON contract
/// of <c>scripts/detect-state.ps1</c> (line 250-273).
/// </summary>
public sealed record StateDetectResult
{
    public required int WorkItemId { get; init; }
    public required string WorkItemType { get; init; }
    public required string WorkItemState { get; init; }
    public required string WorkItemTitle { get; init; }

    /// <summary>User intent: <c>new</c>, <c>redo</c>, or <c>resume</c>.</summary>
    public required string Intent { get; init; }

    /// <summary>SDLC phase from <see cref="Polyphony.Routing.PhaseDetector"/>.</summary>
    public required string Phase { get; init; }

    public required bool HasPlan { get; init; }

    /// <summary><c>none</c> | <c>complete</c> | <c>ambiguous</c>.</summary>
    public required string PlanStatus { get; init; }

    public required string PlanPath { get; init; }

    /// <summary><c>none</c> | <c>filesystem_fallback</c> | <c>explicit_override</c>.</summary>
    public required string PlanSource { get; init; }

    public required bool HasSeededChildren { get; init; }
    public required bool AnyChildMissingTasks { get; init; }

    /// <summary><c>unseeded</c> | <c>partial</c> | <c>seeded</c>.</summary>
    public required string SeedStatus { get; init; }

    /// <summary>JSON-encoded compact summary of children state counts.</summary>
    public required string ChildrenSummary { get; init; }

    /// <summary><c>not_started</c> | <c>in_progress</c> | <c>done</c> | <c>removed</c>.</summary>
    public required string ImplementationStatus { get; init; }

    public required WorkspaceHint WorkspaceHint { get; init; }

    public required string AdoOrg { get; init; }
    public required string AdoProject { get; init; }
    public required string AdoWorkspace { get; init; }

    public required bool IntentConflict { get; init; }
    public required bool NeedsCleanup { get; init; }

    /// <summary>Whether the feature branch exists on the remote.</summary>
    public required bool FeatureBranchExists { get; init; }

    public required string Error { get; init; }
}

/// <summary>Children state count summary; serialised as the
/// <c>children_summary</c> JSON string field of <see cref="StateDetectResult"/>.</summary>
public sealed record ChildrenStateCounts(int Total, int Done, int Doing, int Todo);

