namespace Polyphony.Configuration;

/// <summary>
/// Structured result from <see cref="ConfigValidator.Validate"/>.
/// Errors block execution; warnings are informational.
/// </summary>
public sealed record ConfigValidationResult
{
    public required bool IsValid { get; init; }
    public required ConfigValidationDiagnostic[] Errors { get; init; }
    public required ConfigValidationDiagnostic[] Warnings { get; init; }
}

/// <summary>
/// A single validation diagnostic with rule ID, message, and severity.
/// </summary>
public sealed record ConfigValidationDiagnostic
{
    public required string RuleId { get; init; }
    public required string Message { get; init; }
    public required ConfigValidationSeverity Severity { get; init; }
}

/// <summary>
/// Severity level for configuration validation diagnostics.
/// </summary>
public enum ConfigValidationSeverity
{
    Error,
    Warning
}
