namespace Polyphony.Sdlc;

/// <summary>
/// Canonical source-of-record names for per-item guidance extraction.
/// Constants-not-enum mirrors the established polyphony pattern for over-the-wire
/// string vocabularies (see <see cref="Facet"/>, <see cref="ActionableExecutor"/>,
/// <see cref="ExecutionMode"/>).
/// </summary>
/// <remarks>
/// <para>
/// Per-item guidance is a small, free-form string of context the operator wants
/// the agent to see for a specific work item — e.g. <c>"Use the Foo library, not
/// Bar"</c> or <c>"Reviewer: pay extra attention to error messages"</c>. The
/// source determines where the driver reads it from.
/// </para>
/// <para>
/// <see cref="DescriptionBlock"/> works on every platform (GitHub Issues, ADO,
/// anything with a description field) and is the default. <see cref="AdoField"/>
/// is the opt-in upgrade for workspaces that have stood up a dedicated ADO
/// custom field for guidance.
/// </para>
/// </remarks>
public static class GuidanceSource
{
    /// <summary>
    /// Default. Driver extracts guidance from a fenced HTML comment block in the
    /// work item's description (<c>System.Description</c>):
    /// <code>
    /// &lt;!-- polyphony:guidance --&gt;
    /// ... arbitrary text ...
    /// &lt;!-- /polyphony:guidance --&gt;
    /// </code>
    /// Description outside the block is NOT injected — it is not under the
    /// prompt-injection trust boundary.
    /// </summary>
    public const string DescriptionBlock = "description_block";

    /// <summary>
    /// Opt-in. Driver reads guidance from a dedicated ADO custom field whose
    /// name is configured at policy-load time.
    /// </summary>
    public const string AdoField = "ado_field";

    /// <summary>
    /// Returns true when <paramref name="value"/> is one of the canonical
    /// guidance-source strings. Returns false for null, empty, whitespace, or
    /// unknown strings.
    /// </summary>
    public static bool IsValid(string? value) =>
        value == DescriptionBlock || value == AdoField;
}
