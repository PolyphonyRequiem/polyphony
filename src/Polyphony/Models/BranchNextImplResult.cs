using System.Text.Json.Serialization;

namespace Polyphony;

/// <summary>
/// Result of <c>polyphony branch next-impl</c>. Mirrors the JSON shape of
/// the legacy <c>scripts/impl-router.ps1</c>. JSON wire key
/// <c>current_pg</c> is preserved via
/// <see cref="JsonPropertyNameAttribute"/> until the workflow rewire PR
/// ships.
/// </summary>
public sealed record BranchNextImplResult
{
    public required string Action { get; init; }
    public required int PrimaryId { get; init; }
    public required string PrimaryTitle { get; init; }
    public required string PrimaryType { get; init; }
    public required int ContainerId { get; init; }
    public required string ContainerTitle { get; init; }
    public required string ContainerType { get; init; }
    public required int RemainingCount { get; init; }

    [JsonPropertyName("current_pg")]
    public required string CurrentMergeGroup { get; init; }

    public required string BranchName { get; init; }
    public required string AdoWorkspace { get; init; }
    public string? Error { get; init; }
}
