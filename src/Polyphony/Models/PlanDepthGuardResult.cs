namespace Polyphony;

/// <summary>
/// Output of <c>polyphony plan depth-guard</c>. Mirrors the JSON contract previously
/// emitted by <c>scripts/depth-guard.ps1</c>; consumed by <c>plan-level.yaml</c>'s
/// <c>depth_guard</c> route, which branches on <see cref="Allowed"/>.
/// </summary>
public sealed record PlanDepthGuardResult
{
    /// <summary>True when <c>Depth &lt; MaxDepth</c>.</summary>
    public required bool Allowed { get; init; }

    /// <summary>The depth that was checked.</summary>
    public required int Depth { get; init; }

    /// <summary>The maximum allowed recursion depth.</summary>
    public required int MaxDepth { get; init; }

    /// <summary>Levels remaining before the cap (0 when exhausted).</summary>
    public required int Remaining { get; init; }

    /// <summary>Operator-facing summary suitable for human gate prompts.</summary>
    public required string Message { get; init; }
}
