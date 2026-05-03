namespace Polyphony;

/// <summary>
/// Exit code constants used by all Polyphony CLI commands.
/// Conductor shell scripts branch on these codes to route workflow control.
/// </summary>
public static class ExitCodes
{
    /// <summary>
    /// Command completed successfully; JSON output is valid.
    /// </summary>
    public const int Success = 0;

    /// <summary>
    /// Routing or validation logic determined an invalid state
    /// (e.g., invalid lifecycle event, illegal state transition).
    /// </summary>
    public const int RoutingFailure = 1;

    /// <summary>
    /// Process config file is missing, malformed, or invalid.
    /// </summary>
    public const int ConfigError = 2;

    /// <summary>
    /// Twig SQLite cache is inaccessible or the requested work item is not found.
    /// </summary>
    public const int CacheError = 3;

    /// <summary>
    /// One or more critical health checks failed (polyphony health).
    /// </summary>
    public const int HealthCheckFailed = 4;
}
