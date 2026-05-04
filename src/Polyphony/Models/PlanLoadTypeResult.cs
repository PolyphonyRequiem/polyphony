namespace Polyphony;

/// <summary>
/// Output of <c>polyphony plan load-type</c>. Mirrors the JSON contract emitted by
/// <c>scripts/load-type-context.ps1</c>; consumed by <c>plan-level.yaml</c>'s
/// <c>type_loader</c> step. The architect agent uses these fields to ground its
/// planning prompt in type-specific definitions, templates, and decomposition guidance.
/// </summary>
/// <remarks>
/// On success: <see cref="Type"/>, <see cref="Definition"/>, <see cref="Template"/>,
/// and <see cref="DecompositionGuidance"/> are populated; <see cref="Error"/> is null.
/// On failure: the verb returns a non-zero exit code; <see cref="Error"/> carries
/// the diagnostic, and the empty-string defaults stand in for the missing values.
/// The workflow YAML routes on the process exit code, not on a payload key.
/// </remarks>
public sealed record PlanLoadTypeResult
{
    public required string Type { get; init; }
    public required string Definition { get; init; }
    public required string Template { get; init; }
    public required string DecompositionGuidance { get; init; }
    public string? Error { get; init; }
}
