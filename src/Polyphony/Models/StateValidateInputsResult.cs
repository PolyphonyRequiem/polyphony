namespace Polyphony;

/// <summary>
/// One row in a <see cref="StateValidateInputsResult"/>: a single declared input
/// with its supplied / missing status.
/// </summary>
public sealed record StateValidateInputsDiagnostic
{
    /// <summary>The input name as declared in the workflow YAML's <c>workflow.input.X</c>.</summary>
    public required string Name { get; init; }

    /// <summary>True when the input is declared <c>required: true</c>.</summary>
    public required bool Required { get; init; }

    /// <summary>True when a value was supplied via <c>--input</c> at conductor dispatch time.</summary>
    public required bool Supplied { get; init; }

    /// <summary>The default value declared in YAML (when present and the input is not required).</summary>
    public string? Default { get; init; }

    /// <summary>Human-readable reason this row is failing, when applicable.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Output of <c>polyphony state validate-inputs</c>: the local workaround for
/// the deferred conductor-side input-required enforcement (issue #188). Reads
/// the workflow YAML's <c>workflow.input</c> schema and reports which required
/// inputs were not supplied via <c>--input</c>.
/// </summary>
/// <remarks>
/// Routing-style envelope: the verb ALWAYS exits 0; consumers branch on
/// <c>ready</c>. <c>action</c> is <c>"ok"</c> when ready, <c>"error"</c>
/// otherwise, so a single conductor route on <c>action == "error"</c> covers
/// both this verb and the verb-layer <see cref="RequiredInputErrorResult"/>.
/// </remarks>
public sealed record StateValidateInputsResult
{
    /// <summary>True when every <c>required: true</c> input has a supplied value.</summary>
    public required bool Ready { get; init; }

    /// <summary>One-line summary suitable for a gate prompt.</summary>
    public required string Summary { get; init; }

    /// <summary>Routing signal: <c>"ok"</c> when ready, <c>"error"</c> otherwise.</summary>
    public required string Action { get; init; }

    /// <summary>Path to the workflow YAML that was inspected.</summary>
    public required string WorkflowYaml { get; init; }

    /// <summary>Per-input diagnostics in declaration order.</summary>
    public required IReadOnlyList<StateValidateInputsDiagnostic> Inputs { get; init; }

    /// <summary>Names of required inputs that are missing.</summary>
    public required string[] MissingRequiredInputs { get; init; }

    /// <summary>Names of supplied inputs that are not declared in the workflow's input schema.</summary>
    public required string[] UnknownInputs { get; init; }

    /// <summary>Filled when the workflow YAML cannot be read or parsed.</summary>
    public string? Error { get; init; }
}
