using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Journal.Reset;

namespace Polyphony.Commands;

public sealed partial class ResetCommands
{
    [Command("root")]
    [VerbResult(typeof(ResetRootResult))]
    [JournaledAction(Action = "reset_root")]
    [MutatesResource(ResourceKind.GitHubPr)]
    [MutatesResource(ResourceKind.AdoPr)]
    [MutatesResource(ResourceKind.GitWorktree)]
    [MutatesResource(ResourceKind.GitBranch)]
    [MutatesResource(ResourceKind.AdoWorkItemTag)]
    [MutatesResource(ResourceKind.ManifestFile)]
    [MutatesResource(ResourceKind.PlanFile)]
    [MutatesResource(ResourceKind.LockFile)]
    [MayObserveResource(ResourceKind.ManifestFile)]
    [MayObserveResource(ResourceKind.PlanFile)]
    [MayObserveResource(ResourceKind.LockFile)]
    public Task<int> ResetRoot(
        int root = RequiredInput.MissingInt,
        bool execute = false,
        bool allowUnjournaled = false,
        bool forceMutated = false,
        bool skipState = false,
        string comment = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reset root",
            ("--root", root == RequiredInput.MissingInt)) is { } halt)
        {
            return Task.FromResult(halt);
        }

        return JournalCommandSupport.RunWithCapturedResultAsync<ResetRootResult, ResetRootPayload>(
            _journalDecorator,
            _runContext,
            "reset_root",
            JournalCommandSupport.WorkItemTarget(root),
            innerCt => ResetRootCoreAsync(root, execute, allowUnjournaled, forceMutated, skipState, comment, innerCt),
            PolyphonyJsonContext.Default.ResetRootResult,
            (_, result) => new ResetRootPayload
            {
                Root = result?.Root ?? root,
                DryRun = result?.DryRun ?? !execute,
                Succeeded = result?.Success ?? false,
                WasMutated = result is not null && !result.DryRun && result.DeletedTargets.Count > 0,
                StepsCompleted = result?.StepsCompleted ?? [],
                StepsFailed = result?.StepsFailed ?? [],
                StateSkipped = result?.StateSkipped ?? skipState,
                Error = result?.Error,
            },
            PolyphonyJsonContext.Default.ResetRootPayload,
            payload => payload.Succeeded,
            payload => payload.WasMutated,
            SelectResetRootEffects,
            ct,
            rootId: root,
            workItemId: root);
    }

    private async Task<int> ResetRootCoreAsync(
        int root,
        bool execute,
        bool allowUnjournaled,
        bool forceMutated,
        bool skipState,
        string comment,
        CancellationToken ct)
    {
        var executor = _projectionResetExecutor
            ?? throw new InvalidOperationException("Projection reset executor is not configured.");

        var result = await executor.ExecuteAsync(
            root,
            new ProjectionResetExecutionOptions
            {
                Execute = execute,
                AllowUnjournaled = allowUnjournaled,
                ForceMutated = forceMutated,
                SkipState = skipState,
                Comment = comment,
            },
            ct).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ResetRootResult));
        return ExitCodes.Success;
    }
}
