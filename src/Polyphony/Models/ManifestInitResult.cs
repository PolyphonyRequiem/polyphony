namespace Polyphony;

/// <summary>JSON output for <c>polyphony manifest init</c>.</summary>
public sealed record ManifestInitResult
{
    /// <summary>Absolute or relative path the manifest was written to.</summary>
    public required string Path { get; init; }

    /// <summary>The run's apex (focus) work-item id.</summary>
    public required int RootId { get; init; }

    /// <summary>The platform-qualified project identifier.</summary>
    public required string PlatformProject { get; init; }

    /// <summary>True iff the file was newly created on this invocation.</summary>
    public required bool Created { get; init; }

    /// <summary>The recorded creator (workflow author / operator).</summary>
    public required string CreatedBy { get; init; }

    /// <summary>The topology hash of the empty merge-group set.</summary>
    public required string TopologyHash { get; init; }

    /// <summary>Diagnostic message (e.g. when --force overwrote an existing file).</summary>
    public string? Message { get; init; }

    /// <summary>Validation error when init fails.</summary>
    public string? Error { get; init; }
}
