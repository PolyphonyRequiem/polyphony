namespace Polyphony.Journal;

/// <summary>
/// Manual journaling wrapper for state-mutating verbs.
/// ConsoleAppFramework does not expose a first-class decorator hook for command methods,
/// so Phase 1A keeps the wrapper explicit and AOT-safe: verbs call this helper directly
/// when they opt into <see cref="JournaledActionAttribute"/> in Phase 1B.
/// </summary>
public sealed class JournaledActionDecorator(IJournalStore store)
{
    private readonly IJournalStore _store = store;

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
}
