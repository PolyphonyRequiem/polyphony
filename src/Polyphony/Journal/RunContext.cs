namespace Polyphony.Journal;

/// <summary>
/// Resolves the conductor run id used to correlate journal rows with the
/// workflow event log. When no workflow-supplied run id is present, a
/// process-scoped fallback keeps ad-hoc CLI invocations journalable.
/// </summary>
public sealed class RunContext
{
    public const string RunIdEnvironmentVariable = "POLYPHONY_RUN_ID";
    private static readonly string FallbackRunId = $"manual_{Guid.NewGuid():N}";

    public RunContext()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    internal RunContext(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        RunId = runId;
    }

    internal RunContext(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var configured = getEnvironmentVariable(RunIdEnvironmentVariable);
        RunId = string.IsNullOrWhiteSpace(configured)
            ? FallbackRunId
            : configured;
    }

    public string RunId { get; }
}
