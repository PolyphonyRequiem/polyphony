namespace Polyphony.Models;

/// <summary>
/// Result envelope for <c>polyphony plan write-plan</c>. Reports the absolute
/// path written, the byte count, and a SHA-256 of the content for downstream
/// integrity checks. Set <see cref="Error"/> on failure (verb still exits 0
/// so the workflow can route on the JSON payload).
/// </summary>
public sealed class PlanWritePlanResult
{
    /// <summary>Work-item id whose plan was written.</summary>
    public required int ItemId { get; init; }

    /// <summary>Absolute path to the written file (e.g. <c>plans/plan-1234.md</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Number of UTF-8 bytes written. Zero on error.</summary>
    public required long BytesWritten { get; init; }

    /// <summary>Lowercase hex SHA-256 of the written content. Empty on error.</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>True if the file existed and the content was identical (no rewrite needed). Always false on error.</summary>
    public required bool Unchanged { get; init; }

    /// <summary>Error message; null on success.</summary>
    public string? Error { get; init; }
}
