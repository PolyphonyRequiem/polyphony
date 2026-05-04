namespace Polyphony;

/// <summary>
/// Output of <c>polyphony policy validate</c>. Echoes the source path, schema version,
/// and whether the policy is fully valid. When <see cref="Errors"/> is non-empty,
/// the verb returns <see cref="ExitCodes.ConfigError"/>.
/// </summary>
public sealed record PolicyValidateResult
{
    public required bool Valid { get; init; }

    public required string SourcePath { get; init; }

    public int? SchemaVersion { get; init; }

    public required string[] Errors { get; init; }

    public required string[] Warnings { get; init; }
}
