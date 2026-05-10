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

    /// <summary>
    /// How the manifest path was resolved: <c>"derived"</c> (from --root-id
    /// via PolyphonyStatePaths), <c>"explicit"</c> (caller supplied --path),
    /// or <c>"default_legacy"</c> (no --root-id, no --path; fell back to
    /// <c>.polyphony/run.yaml</c>). Always populated on success; populated
    /// on error when the failure happened after path resolution.
    /// </summary>
    public string? PathSource { get; init; }

    /// <summary>
    /// Structured error tag for workflow gates to route on. Populated only
    /// on error paths. Known values: <c>invalid_root_id</c>,
    /// <c>manifest_path_resolution_failed</c>, <c>manifest_not_found</c>,
    /// <c>manifest_malformed</c>, <c>manifest_root_mismatch</c>.
    /// </summary>
    public string? ErrorCode { get; init; }
}
