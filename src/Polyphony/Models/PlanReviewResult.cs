namespace Polyphony;

/// <summary>
/// Result emitted by <c>polyphony plan review</c>. Aggregates technical and
/// readability reviewer outputs and decides whether the plan-level workflow
/// should loop back to the architect or proceed to the human plan-approval
/// gate.
///
/// <para>Pass criteria (any one wins):</para>
/// <list type="bullet">
///   <item><description><c>average_score &gt;= 90</c> (passByScore)</description></item>
///   <item><description><c>blocking_issue_count == 0</c> (passByNoBlocking)</description></item>
///   <item><description><c>revision_cycles_completed &gt;= max_cycles</c> (capHit — escapes oscillation)</description></item>
/// </list>
///
/// When <see cref="ForcedByCap"/> is <c>true</c>, the score thresholds were never
/// met but the workflow is bailing out so a human can decide. The plan-approval
/// prompt surfaces this prominently.
/// </summary>
public sealed record PlanReviewResult
{
    public required int AverageScore { get; init; }

    public required int TechnicalScore { get; init; }

    public required int ReadabilityScore { get; init; }

    public required int RevisionCyclesCompleted { get; init; }

    public required int BlockingIssueCount { get; init; }

    public required string CombinedFeedback { get; init; }

    public required bool Passed { get; init; }

    public required bool ForcedByCap { get; init; }
}
