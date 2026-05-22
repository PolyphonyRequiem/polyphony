namespace Polyphony.Journal;

/// <summary>
/// Resolves the conductor run id used to correlate journal rows with the
/// workflow event log. The launcher (W1) exports
/// <see cref="RunIdEnvironmentVariable"/> so every nested polyphony
/// subprocess sees the same lineage; when the variable is unset (ad-hoc
/// CLI use, isolated unit tests), a process-scoped <c>manual_*</c>
/// fallback keeps the journal write path runnable. The <c>manual_</c>
/// prefix is deliberate so downstream code (W5 mutation guard, manifest
/// init's mint-real-ULID branch) can distinguish a launcher-stamped
/// lineage from an unsanctioned one.
/// </summary>
public sealed class RunContext
{
    public const string RunIdEnvironmentVariable = "POLYPHONY_RUN_ID";

    /// <summary>
    /// Prefix used by the unstamped-process fallback. Tests, manifest
    /// init, and the W5 mutation guard all key on this prefix.
    /// </summary>
    public const string ManualLineagePrefix = "manual_";

    /// <summary>Symbolic source values surfaced through <see cref="RunIdSource"/>.</summary>
    public static class Sources
    {
        public const string Environment = "env";
        public const string ManualFallback = "manual_fallback";
        public const string Explicit = "explicit";
    }

    private static readonly string FallbackRunId = $"{ManualLineagePrefix}{Guid.NewGuid():N}";

    public RunContext()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    internal RunContext(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        RunId = runId;
        RunIdSource = Sources.Explicit;
    }

    internal RunContext(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var configured = getEnvironmentVariable(RunIdEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            RunId = FallbackRunId;
            RunIdSource = Sources.ManualFallback;
        }
        else
        {
            RunId = configured;
            RunIdSource = Sources.Environment;
        }
    }

    public string RunId { get; }

    /// <summary>
    /// How <see cref="RunId"/> was resolved. One of
    /// <see cref="Sources.Environment"/>, <see cref="Sources.ManualFallback"/>,
    /// or <see cref="Sources.Explicit"/> (test-supplied).
    /// </summary>
    public string RunIdSource { get; }

    /// <summary>
    /// True when <see cref="RunId"/> falls back to the unstamped
    /// <c>manual_*</c> shape — i.e., the launcher did not export
    /// <c>POLYPHONY_RUN_ID</c>. Consumers writing lineage to durable
    /// state (manifest, journal grounding effects) should mint a real
    /// ULID via <see cref="RunIdMint"/> in this case rather than
    /// persisting the ephemeral fallback.
    /// </summary>
    public bool HasManualLineage => RunIdSource == Sources.ManualFallback
        || (RunId is not null && RunId.StartsWith(ManualLineagePrefix, StringComparison.Ordinal));
}
