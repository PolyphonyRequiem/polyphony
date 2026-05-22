using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Journal.Reset;

namespace Polyphony.Commands;

public sealed partial class ResetCommands
{
    [Command("apex")]
    [VerbResult(typeof(ResetApexResult))]
    [JournaledAction(Action = "reset_apex")]
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
    public Task<int> ResetApex(
        int apex = RequiredInput.MissingInt,
        int root = RequiredInput.MissingInt,
        bool execute = false,
        string strategy = ProjectionResetStrategy.Projection,
        bool allowUnjournaled = false,
        bool forceMutated = false,
        bool skipState = false,
        string comment = "",
        CancellationToken ct = default)
    {
        if (apex != RequiredInput.MissingInt
            && root != RequiredInput.MissingInt
            && apex != root)
        {
            Console.Error.WriteLine("--apex and --root must match when both are provided.");
            return Task.FromResult(ExitCodes.RoutingFailure);
        }

        var resolvedApex = apex != RequiredInput.MissingInt ? apex : root;
        if (RequiredInput.HaltIfMissing("reset apex",
            ("--apex", resolvedApex == RequiredInput.MissingInt)) is { } halt)
        {
            return Task.FromResult(halt);
        }

        return JournalCommandSupport.RunWithCapturedResultAsync<ResetApexResult, ResetRootPayload>(
            _journalDecorator,
            _runContext,
            "reset_apex",
            JournalCommandSupport.WorkItemTarget(resolvedApex),
            innerCt => ResetApexCoreAsync(resolvedApex, execute, strategy, allowUnjournaled, forceMutated, skipState, comment, innerCt),
            PolyphonyJsonContext.Default.ResetApexResult,
            (_, result) => new ResetRootPayload
            {
                Root = result?.Root ?? resolvedApex,
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
            rootId: resolvedApex,
            workItemId: resolvedApex);
    }

    public Task<int> ResetRoot(
        int root = RequiredInput.MissingInt,
        bool execute = false,
        bool skipState = false,
        string comment = "",
        CancellationToken ct = default)
        => ResetApex(
            apex: root,
            execute: execute,
            strategy: ProjectionResetStrategy.Pattern,
            allowUnjournaled: false,
            forceMutated: false,
            skipState: skipState,
            comment: comment,
            ct: ct);

    private async Task<int> ResetApexCoreAsync(
        int apex,
        bool execute,
        string strategy,
        bool allowUnjournaled,
        bool forceMutated,
        bool skipState,
        string comment,
        CancellationToken ct)
    {
        ResetApexResult result;
        if (string.Equals(strategy, ProjectionResetStrategy.Projection, StringComparison.OrdinalIgnoreCase))
        {
            var executor = _projectionResetExecutor
                ?? throw new InvalidOperationException("Projection reset executor is not configured.");
            result = await executor.ExecuteAsync(
                apex,
                new ProjectionResetExecutionOptions
                {
                    Execute = execute,
                    AllowUnjournaled = allowUnjournaled,
                    ForceMutated = forceMutated,
                    SkipState = skipState,
                    Comment = comment,
                },
                ct).ConfigureAwait(false);
        }
        else if (string.Equals(strategy, ProjectionResetStrategy.Pattern, StringComparison.OrdinalIgnoreCase))
        {
            result = await ResetApexPatternCoreAsync(apex, execute, skipState, comment, ct).ConfigureAwait(false);
        }
        else
        {
            result = new ResetApexResult
            {
                Root = apex,
                Success = false,
                DryRun = !execute,
                Strategy = strategy,
                Coverage = ProjectionResetCoverage.None,
                FallbackUsed = false,
                StepsCompleted = [],
                StepsFailed = [],
                AttemptedTargets = [],
                DeletedTargets = [],
                FailedTargets = [],
                RemainingResetTargets = [],
                BlockedMutatedTargets = [],
                StateSkipped = skipState,
                Error = $"Unsupported reset strategy '{strategy}'. Expected 'projection' or 'pattern'.",
            };
        }

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ResetApexResult));
        return ExitCodes.Success;
    }

    private async Task<ResetApexResult> ResetApexPatternCoreAsync(
        int apex,
        bool execute,
        bool skipState,
        string comment,
        CancellationToken ct)
    {
        var stepsCompleted = new List<string>();
        var stepsFailed = new List<string>();
        ResetPrsResult? prs = null;
        ResetWorktreesResult? worktrees = null;
        ResetBranchesResult? branches = null;
        ResetFacetsResult? facets = null;
        ResetManifestResult? manifest = null;
        ResetStateResult? state = null;
        string? haltReason = null;

        try
        {
            prs = await RunPrsAsync(apex, execute, comment, ct).ConfigureAwait(false);
            if (prs.Success) stepsCompleted.Add("prs");
            else { stepsFailed.Add("prs"); haltReason = $"prs: {prs.Error}"; }

            if (haltReason is null)
            {
                worktrees = await RunWorktreesAsync(apex, execute, ct).ConfigureAwait(false);
                if (worktrees.Success) stepsCompleted.Add("worktrees");
                else { stepsFailed.Add("worktrees"); haltReason = $"worktrees: {worktrees.Error}"; }
            }

            if (haltReason is null)
            {
                branches = await RunBranchesAsync(apex, execute, ct).ConfigureAwait(false);
                if (branches.Success) stepsCompleted.Add("branches");
                else { stepsFailed.Add("branches"); haltReason = $"branches: {branches.Error}"; }
            }

            if (haltReason is null)
            {
                facets = await RunFacetsAsync(apex, execute, ct).ConfigureAwait(false);
                if (facets.Success) stepsCompleted.Add("facets");
                else { stepsFailed.Add("facets"); haltReason = $"facets: {facets.Error}"; }
            }

            if (haltReason is null)
            {
                manifest = await RunManifestAsync(apex, execute, ct).ConfigureAwait(false);
                if (manifest.Success) stepsCompleted.Add("manifest");
                else { stepsFailed.Add("manifest"); haltReason = $"manifest: {manifest.Error}"; }
            }

            if (haltReason is null && !skipState)
            {
                state = await RunStateAsync(apex, execute, ct).ConfigureAwait(false);
                if (state.Success) stepsCompleted.Add("state");
                else { stepsFailed.Add("state"); haltReason = $"state: {state.Error}"; }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            haltReason = $"pattern reset threw: {ex.Message}";
        }

        return new ResetApexResult
        {
            Root = apex,
            Success = stepsFailed.Count == 0 && haltReason is null,
            DryRun = !execute,
            Strategy = ProjectionResetStrategy.Pattern,
            Coverage = ProjectionResetCoverage.None,
            FallbackUsed = false,
            StepsCompleted = stepsCompleted,
            StepsFailed = stepsFailed,
            AttemptedTargets = [],
            DeletedTargets = [],
            FailedTargets = [],
            RemainingResetTargets = [],
            BlockedMutatedTargets = [],
            Prs = prs,
            Worktrees = worktrees,
            Branches = branches,
            Facets = facets,
            Manifest = manifest,
            State = state,
            StateSkipped = skipState,
            Error = haltReason,
        };
    }

    private async Task<ResetPrsResult> RunPrsAsync(int apex, bool execute, string comment, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetPrs(apex, execute, comment, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetPrsResult)
            ?? throw new InvalidOperationException("reset prs returned unparseable JSON");
    }

    private async Task<ResetWorktreesResult> RunWorktreesAsync(int apex, bool execute, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetWorktrees(apex, execute, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetWorktreesResult)
            ?? throw new InvalidOperationException("reset worktrees returned unparseable JSON");
    }

    private async Task<ResetBranchesResult> RunBranchesAsync(int apex, bool execute, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetBranches(apex, execute, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetBranchesResult)
            ?? throw new InvalidOperationException("reset branches returned unparseable JSON");
    }

    private async Task<ResetFacetsResult> RunFacetsAsync(int apex, bool execute, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetFacets(apex, execute, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetFacetsResult)
            ?? throw new InvalidOperationException("reset facets returned unparseable JSON");
    }

    private async Task<ResetManifestResult> RunManifestAsync(int apex, bool execute, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetManifest(apex, execute, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetManifestResult)
            ?? throw new InvalidOperationException("reset manifest returned unparseable JSON");
    }

    private async Task<ResetStateResult> RunStateAsync(int apex, bool execute, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetState(apex, execute, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetStateResult)
            ?? throw new InvalidOperationException("reset state returned unparseable JSON");
    }

    private static async Task<string> CaptureAsync(Func<Task<int>> action)
    {
        var prior = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            _ = await action().ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(prior);
        }

        return writer.ToString().Trim();
    }
}
