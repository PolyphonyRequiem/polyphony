namespace Polyphony.Sdlc;

/// <summary>
/// Observable disposition signals for a single work item, indexed by
/// <see cref="RequirementKind"/>. Produced by the consumer (e.g. <c>state detect</c>)
/// from inspectable evidence — plan files on disk, child seed status, PR/branch
/// state, etc. Consumed by <see cref="RequirementSetReducer"/> to overlay onto a
/// derived <see cref="RequirementSet"/> before computing readiness.
/// </summary>
/// <remarks>
/// <para>
/// Kinds absent from <see cref="Observed"/> default to <see cref="Disposition.Needed"/>.
/// This is the safe default — observers should only emit a stronger disposition when
/// they have direct evidence for it.
/// </para>
/// <para>
/// Observers are intentionally restricted from emitting <see cref="Disposition.Ready"/>:
/// readiness is a derived property that depends on the prerequisite-edge graph and is
/// computed by the reducer, not by external observation.
/// </para>
/// </remarks>
public sealed record ObservedRequirementState
{
    /// <summary>
    /// Per-kind observed disposition. Keys must be valid <see cref="RequirementKind"/>
    /// strings; values must be valid <see cref="Disposition"/> strings other than
    /// <see cref="Disposition.Ready"/>. Validated lazily by <see cref="Validate"/>.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Observed { get; init; }

    /// <summary>An empty observation (everything defaults to <see cref="Disposition.Needed"/>).</summary>
    public static ObservedRequirementState Empty { get; } =
        new() { Observed = new Dictionary<string, string>(StringComparer.Ordinal) };

    /// <summary>
    /// Returns the observed disposition for <paramref name="kind"/>, or
    /// <see cref="Disposition.Needed"/> when the kind is not present.
    /// </summary>
    public string GetOrNeeded(string kind) =>
        Observed.TryGetValue(kind, out var d) ? d : Disposition.Needed;

    /// <summary>
    /// Validate keys (must be known requirement kinds) and values (must be valid
    /// dispositions other than <see cref="Disposition.Ready"/>). Returns the list
    /// of errors; empty list means valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (kind, disposition) in Observed)
        {
            if (!RequirementKind.IsValid(kind))
            {
                errors.Add($"Observed kind '{kind}' is not a valid requirement kind.");
                continue;
            }
            if (!Disposition.IsValid(disposition))
            {
                errors.Add($"Observed disposition '{disposition}' for kind '{kind}' is not a valid disposition.");
                continue;
            }
            if (disposition == Disposition.Ready)
            {
                errors.Add(
                    $"Observed disposition for kind '{kind}' is '{Disposition.Ready}'; " +
                    $"readiness is derived by the reducer and must not be supplied by observers.");
            }
        }
        return errors;
    }

    /// <summary>
    /// Convenience builder. Pairs are (kind, disposition). Uses ordinal comparison
    /// for keys; later pairs overwrite earlier ones with the same key.
    /// </summary>
    public static ObservedRequirementState From(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in pairs) dict[kvp.Key] = kvp.Value;
        return new ObservedRequirementState { Observed = dict };
    }
}
