namespace Polyphony;

/// <summary>
/// Output envelope for <c>polyphony reset state --apex N</c> — stamps the
/// per-apex run-watermark tag (<c>polyphony:run-started-at=&lt;ISO-8601&gt;</c>)
/// on the apex root work item.
///
/// <para>This is the ONLY writer of the watermark. Read-side filtering lives
/// in <see cref="Sdlc.Observers.PlanObserver"/> +
/// <see cref="Commands.StateCommands"/>; see
/// <c>docs/decisions/run-reset.md</c> for the full design.</para>
///
/// <para>Routing-style envelope: the verb always exits 0; callers route on
/// <see cref="Success"/> + <see cref="Error"/>.</para>
/// </summary>
public sealed record ResetStateResult
{
    /// <summary>Apex root work-item ID (mirrors <c>--apex</c>).</summary>
    public required int Apex { get; init; }

    /// <summary>True when the watermark was successfully stamped (or already correct in dry-run).</summary>
    public required bool Success { get; init; }

    /// <summary>True when this was a dry-run preview (no writes performed).</summary>
    public required bool DryRun { get; init; }

    /// <summary>
    /// The previous watermark value parsed from the apex's tag set, formatted
    /// as ISO-8601 UTC. Null when no prior <c>polyphony:run-started-at</c>
    /// tag existed.
    /// </summary>
    public string? PreviousWatermark { get; init; }

    /// <summary>
    /// The newly-stamped watermark value (UTC). Always emitted when
    /// <see cref="Success"/> is true — even in dry-run mode, this is the
    /// value that would have been written.
    /// </summary>
    public string? NewWatermark { get; init; }

    /// <summary>
    /// Number of duplicate <c>polyphony:run-started-at</c> tags that were
    /// removed before stamping the fresh value. Normally 0; non-zero
    /// indicates an earlier reset bug or operator hand-edit.
    /// </summary>
    public int RemovedDuplicateTags { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}
