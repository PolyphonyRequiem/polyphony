namespace Polyphony.Models;

/// <summary>
/// JSON output of <c>polyphony guidance extract --work-item N</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Guidance"/> is null when no guidance is present for the work
/// item, distinguishable from the empty string. <see cref="GuidancePresent"/>
/// is the boolean projection workflows can route on without re-checking the
/// nullable field.
/// </para>
/// </remarks>
public sealed record GuidanceExtractResult
{
    /// <summary>The work item the guidance was extracted from.</summary>
    public required int WorkItemId { get; init; }

    /// <summary>One of the <see cref="Polyphony.Sdlc.GuidanceSource"/>
    /// constants — the resolved source-of-record after policy layering.</summary>
    public required string Source { get; init; }

    /// <summary>The extracted guidance text, or null when none is present.
    /// Multiple description blocks are concatenated with <c>\n\n---\n\n</c>.</summary>
    public string? Guidance { get; init; }

    /// <summary>Convenience boolean: true iff <see cref="Guidance"/> is non-null.</summary>
    public required bool GuidancePresent { get; init; }
}
