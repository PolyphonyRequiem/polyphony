namespace Polyphony.Sdlc;

/// <summary>
/// Canonical facet name string constants. A facet is a kind of work an item
/// carries; an item carries a SET of facets (zero or more). Loaded per type
/// from <c>process-config.yaml</c>.
/// </summary>
public static class Facet
{
    public const string Plannable = "plannable";
    public const string Actionable = "actionable";
    public const string Implementable = "implementable";

    /// <summary>
    /// Returns true if <paramref name="value"/> is one of the canonical facet name strings.
    /// </summary>
    public static bool IsValid(string? value) =>
        value is Plannable or Actionable or Implementable;
}

/// <summary>
/// Executor string constants for the actionable facet. Determines whether
/// evidence is required.
/// </summary>
public static class ActionableExecutor
{
    /// <summary>Polyphony performs the action; evidence is required.</summary>
    public const string Polyphony = "polyphony";

    /// <summary>Action is outside polyphony's authority; only satisfaction is recorded.</summary>
    public const string Human = "human";

    /// <summary>
    /// Returns true if <paramref name="value"/> is one of the canonical executor strings.
    /// </summary>
    public static bool IsValid(string? value) =>
        value is Polyphony or Human;
}
