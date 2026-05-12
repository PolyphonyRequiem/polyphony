namespace Polyphony.Models;

/// <summary>
/// Result envelope for <c>polyphony plan write-plan</c>. Reports the absolute
/// path written, the byte count, and a SHA-256 of the content for downstream
/// integrity checks. When the verb is invoked with <c>--children-json</c>, the
/// sidecar fields (<see cref="ChildrenPath"/>, <see cref="ChildrenSha256"/>,
/// <see cref="ChildrenUnchanged"/>) describe the persisted
/// <c>plans/plan-{itemId}.children.json</c> artifact (the seeder's contract
/// surface for re-entry recovery — see AB#3106 dogfood, 2026-05-12). Set
/// <see cref="Error"/> on failure (verb still exits 0 so the workflow can
/// route on the JSON payload).
/// </summary>
public sealed class PlanWritePlanResult
{
    /// <summary>Work-item id whose plan was written.</summary>
    public required int ItemId { get; init; }

    /// <summary>Absolute path to the written plan markdown file (e.g. <c>plans/plan-1234.md</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Number of UTF-8 bytes written for the plan markdown. Zero on error.</summary>
    public required long BytesWritten { get; init; }

    /// <summary>Lowercase hex SHA-256 of the written plan markdown. Empty on error.</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>True if the plan markdown existed and was identical (no rewrite needed). Always false on error.</summary>
    public required bool Unchanged { get; init; }

    /// <summary>
    /// Absolute path to the written sidecar (<c>plans/plan-{itemId}.children.json</c>)
    /// when <c>--children-json</c> was supplied; empty string otherwise. The sidecar
    /// is the durable contract between architect and seeder — committed alongside
    /// the plan markdown so a re-entering workflow execution (whose
    /// <c>architect.output.children</c> is no longer in context) can still seed.
    /// </summary>
    public string ChildrenPath { get; init; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the written sidecar JSON; empty when no sidecar was written.</summary>
    public string ChildrenSha256 { get; init; } = string.Empty;

    /// <summary>True if the sidecar existed and was byte-identical to the new content; false otherwise (including when no sidecar was written).</summary>
    public bool ChildrenUnchanged { get; init; }

    /// <summary>True when the caller did not pass <c>--children-json</c>, so no sidecar emission was attempted (any pre-existing sidecar is left untouched).</summary>
    public bool ChildrenSkipped { get; init; }

    /// <summary>Error message; null on success.</summary>
    public string? Error { get; init; }
}

