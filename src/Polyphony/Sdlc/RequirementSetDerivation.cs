namespace Polyphony.Sdlc;

/// <summary>
/// Result of <see cref="RequirementSetDeriver.Derive"/>. On success,
/// <see cref="Set"/> is populated and <see cref="Errors"/> is empty.
/// On failure, <see cref="Set"/> is <c>null</c> and <see cref="Errors"/>
/// describes what went wrong. <see cref="Warnings"/> may be present in
/// either case.
/// </summary>
public sealed record RequirementSetDerivation(
    RequirementSet? Set,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True if derivation succeeded; <see cref="Set"/> is non-null.</summary>
    public bool IsValid => Set is not null && Errors.Count == 0;
}
