namespace Polyphony.Journal;

using System.Text.Json;

/// <summary>
/// Manual journaling wrapper for state-mutating verbs.
/// ConsoleAppFramework does not expose a first-class decorator hook for command methods,
/// so Phase 1A keeps the wrapper explicit and AOT-safe: verbs call this helper directly
/// when they opt into <see cref="JournaledActionAttribute"/> in Phase 1B.
/// </summary>
public sealed class JournaledActionDecorator
{
    private readonly IJournalStore _store;
    private readonly bool _failClosedOnManualLineage;

    /// <summary>
    /// Default ctor — keeps the W5 fail-closed guard OFF so existing test
    /// fixtures that construct decorators without an env-stamped run id
    /// continue to work. The production DI registration constructs with
    /// <see cref="WithManualLineageGuard"/> = <c>true</c>.
    /// </summary>
    public JournaledActionDecorator(IJournalStore store)
        : this(store, failClosedOnManualLineage: false)
    {
    }

    /// <summary>
    /// W5 (AB#3279) ctor: when <paramref name="failClosedOnManualLineage"/>
    /// is <c>true</c>, the decorator refuses to invoke any mutating action
    /// whose invocation carries a <see cref="RunContext.ManualLineagePrefix"/>
    /// run id, writes no journal rows, and returns
    /// <see cref="ExitCodes.MissingRunIdLineage"/> after emitting a
    /// structured error envelope to stderr.
    /// </summary>
    public JournaledActionDecorator(IJournalStore store, bool failClosedOnManualLineage)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _failClosedOnManualLineage = failClosedOnManualLineage;
    }

    /// <summary>
    /// True when this decorator's W5 mutation guard is engaged. Surfaced
    /// for test assertions and for diagnostics surfaces that want to
    /// communicate the active posture.
    /// </summary>
    public bool WithManualLineageGuard => _failClosedOnManualLineage;

    public async Task<int> RunWithAsync(
        JournaledActionInvocation invocation,
        Func<CancellationToken, Task<int>> action,
        Func<int, JournalOutcome>? outcomeSelector = null,
        Func<int, string?>? payloadSelector = null,
        Func<int, IReadOnlyList<JournalResourceEffect>>? effectsSelector = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(action);

        // ── W5 (AB#3279) fail-closed mutation guard. ──────────────────────
        // Refuse to mutate when the run id fell back to a manual_* shape
        // (POLYPHONY_RUN_ID was unset). Writes ZERO journal rows so the
        // refusal does not pollute the journal of whichever real lineage
        // the launcher would later stamp. Diagnostic verbs (journal,
        // policy, health, validate-config) don't go through this
        // decorator and are unaffected.
        if (_failClosedOnManualLineage
            && invocation.RunId.StartsWith(RunContext.ManualLineagePrefix, StringComparison.Ordinal))
        {
            EmitManualLineageRefusal(invocation);
            return ExitCodes.MissingRunIdLineage;
        }

        var actionId = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = invocation.RunId,
                RootId = invocation.RootId,
                WorkItemId = invocation.WorkItemId,
                Action = invocation.Action,
                Target = invocation.Target,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PayloadJson = invocation.PayloadJson,
            },
            ct).ConfigureAwait(false);

        try
        {
            var exitCode = await action(ct).ConfigureAwait(false);
            var outcome = (outcomeSelector ?? DefaultOutcomeSelector)(exitCode);
            var errorCode = exitCode == ExitCodes.Success ? null : $"exit_code_{exitCode}";
            await _store.RecordEndAsync(
                actionId,
                outcome,
                errorCode,
                null,
                payloadSelector?.Invoke(exitCode),
                effectsSelector?.Invoke(exitCode),
                CancellationToken.None).ConfigureAwait(false);
            return exitCode;
        }
        catch (OperationCanceledException ex)
        {
            await _store.RecordEndAsync(actionId, JournalOutcome.Failure, "operation_canceled", ex.Message, null, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await _store.RecordEndAsync(actionId, JournalOutcome.Failure, ex.GetType().Name, ex.Message, null, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static JournalOutcome DefaultOutcomeSelector(int exitCode) => exitCode switch
    {
        ExitCodes.Success => JournalOutcome.Success,
        _ => JournalOutcome.Failure,
    };

    private static void EmitManualLineageRefusal(JournaledActionInvocation invocation)
    {
        var envelope = new ManualLineageRefusal
        {
            Error = "missing_run_id_lineage",
            Verb = invocation.Action,
            Target = invocation.Target,
            EnvVar = RunContext.RunIdEnvironmentVariable,
            Message = "Refusing to mutate without POLYPHONY_RUN_ID. The launcher (Invoke-PolyphonySdlc.ps1) mints a ULID and exports it; ad-hoc `polyphony` invocations must export POLYPHONY_RUN_ID first (use a launcher-minted ULID to resume an existing run, or mint a new one for a fresh lineage). Diagnostic verbs (journal, policy, health, validate-config) do not require the env var.",
        };
        Console.Error.WriteLine(JsonSerializer.Serialize(
            envelope, PolyphonyJsonContext.Default.ManualLineageRefusal));
    }
}

/// <summary>
/// W5 (AB#3279): structured envelope written to stderr when the
/// fail-closed mutation guard refuses an invocation.
/// </summary>
public sealed record ManualLineageRefusal
{
    public required string Error { get; init; }
    public required string Verb { get; init; }
    public required string Target { get; init; }
    public required string EnvVar { get; init; }
    public required string Message { get; init; }
}
