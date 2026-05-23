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
        string strategy = ProjectionResetStrategy.Projection,
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
            innerCt => ResetRootCoreAsync(root, execute, strategy, allowUnjournaled, forceMutated, skipState, comment, innerCt),
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
        string strategy,
        bool allowUnjournaled,
        bool forceMutated,
        bool skipState,
        string comment,
        CancellationToken ct)
    {
        ResetRootResult result;
        if (string.Equals(strategy, ProjectionResetStrategy.Projection, StringComparison.OrdinalIgnoreCase))
        {
            var executor = _projectionResetExecutor
                ?? throw new InvalidOperationException("Projection reset executor is not configured.");
            result = await executor.ExecuteAsync(
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
        }
        else if (string.Equals(strategy, ProjectionResetStrategy.Pattern, StringComparison.OrdinalIgnoreCase))
        {
            result = await ResetRootPatternCoreAsync(root, execute, skipState, comment, ct).ConfigureAwait(false);
        }
        else
        {
            result = new ResetRootResult
            {
                Root = root,
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

        // W12 (AB#3293): regardless of pipeline outcome, attempt to
        // tombstone every active lineage for the root. Tombstoning is
        // strictly local journal bookkeeping; it does not depend on
        // remote/twig cleanup actually succeeding. Run it best-effort
        // — a journal hiccup here must not flip a successful reset
        // into a failure (the watermark already advanced).
        var lineageResult = await RunLineageRetireAsync(root, execute, ct).ConfigureAwait(false);
        var stepsCompleted = result.StepsCompleted.ToList();
        var stepsFailed = result.StepsFailed.ToList();
        if (lineageResult.Success)
            stepsCompleted.Add("lineages");
        else
            stepsFailed.Add("lineages");
        result = result with
        {
            Lineages = lineageResult,
            StepsCompleted = stepsCompleted,
            StepsFailed = stepsFailed,
        };

        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ResetRootResult));
        return ExitCodes.Success;
    }

    /// <summary>
    /// W12 (AB#3293): Retire every active (non-tombstoned) lineage
    /// for <paramref name="root"/>. Dry-run reports what WOULD be
    /// retired without writing; execute mode writes
    /// <c>retired_at</c> + reason to each row.
    ///
    /// <para>Reset writes a tombstone, not a delete, so future runs
    /// can see what lineages existed (for diagnostics, attach,
    /// reconcile) while routing verbs treat the retired rows as
    /// foreign.</para>
    /// </summary>
    private async Task<Polyphony.Models.LineageRetireResult> RunLineageRetireAsync(int root, bool execute, CancellationToken ct)
    {
        const string Reason = "reset root";
        if (_journalStore is NullJournalStore)
        {
            return new Polyphony.Models.LineageRetireResult
            {
                Root = root,
                RunId = null,
                AllActive = true,
                Executed = execute,
                Reason = Reason,
                RetiredRunIds = [],
                SkippedRunIds = [],
                Success = true,
            };
        }

        IReadOnlyList<JournalLineage> lineages;
        try
        {
            lineages = await _journalStore.GetLineagesAsync(root, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new Polyphony.Models.LineageRetireResult
            {
                Root = root,
                RunId = null,
                AllActive = true,
                Executed = execute,
                Reason = Reason,
                RetiredRunIds = [],
                SkippedRunIds = [],
                Success = false,
                Error = $"Failed to enumerate lineages: {ex.Message}",
            };
        }

        var active = lineages.Where(l => l.RetiredAt is null).ToList();
        var alreadyRetired = lineages
            .Where(l => l.RetiredAt is not null)
            .Select(l => l.RunId)
            .ToList();
        if (active.Count == 0)
        {
            return new Polyphony.Models.LineageRetireResult
            {
                Root = root,
                RunId = null,
                AllActive = true,
                Executed = execute,
                Reason = Reason,
                RetiredRunIds = [],
                SkippedRunIds = alreadyRetired,
                Success = true,
            };
        }

        var retired = new List<string>();
        if (execute)
        {
            foreach (var lineage in active)
            {
                try
                {
                    var didRetire = await _journalStore.RetireLineageAsync(
                        lineage.RunId, root, Reason, ct).ConfigureAwait(false);
                    if (didRetire) retired.Add(lineage.RunId);
                }
                catch (Exception ex)
                {
                    return new Polyphony.Models.LineageRetireResult
                    {
                        Root = root,
                        RunId = null,
                        AllActive = true,
                        Executed = true,
                        Reason = Reason,
                        RetiredRunIds = retired,
                        SkippedRunIds = alreadyRetired,
                        Success = false,
                        Error = $"Failed to retire '{lineage.RunId}': {ex.Message}",
                    };
                }
            }
        }
        else
        {
            // Dry-run: report what WOULD be retired.
            retired.AddRange(active.Select(l => l.RunId));
        }

        return new Polyphony.Models.LineageRetireResult
        {
            Root = root,
            RunId = null,
            AllActive = true,
            Executed = execute,
            Reason = Reason,
            RetiredRunIds = retired,
            SkippedRunIds = alreadyRetired,
            Success = true,
        };
    }

    private async Task<ResetRootResult> ResetRootPatternCoreAsync(
        int root,
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
            prs = await RunPrsAsync(root, execute, comment, ct).ConfigureAwait(false);
            if (prs.Success) stepsCompleted.Add("prs");
            else { stepsFailed.Add("prs"); haltReason = $"prs: {prs.Error}"; }

            if (haltReason is null)
            {
                worktrees = await RunWorktreesAsync(root, execute, ct).ConfigureAwait(false);
                if (worktrees.Success) stepsCompleted.Add("worktrees");
                else { stepsFailed.Add("worktrees"); haltReason = $"worktrees: {worktrees.Error}"; }
            }

            if (haltReason is null)
            {
                branches = await RunBranchesAsync(root, execute, ct).ConfigureAwait(false);
                if (branches.Success) stepsCompleted.Add("branches");
                else { stepsFailed.Add("branches"); haltReason = $"branches: {branches.Error}"; }
            }

            if (haltReason is null)
            {
                facets = await RunFacetsAsync(root, execute, ct).ConfigureAwait(false);
                if (facets.Success) stepsCompleted.Add("facets");
                else { stepsFailed.Add("facets"); haltReason = $"facets: {facets.Error}"; }
            }

            if (haltReason is null)
            {
                manifest = await RunManifestAsync(root, execute, ct).ConfigureAwait(false);
                if (manifest.Success) stepsCompleted.Add("manifest");
                else { stepsFailed.Add("manifest"); haltReason = $"manifest: {manifest.Error}"; }
            }

            if (haltReason is null && !skipState)
            {
                state = await RunStateAsync(root, execute, ct).ConfigureAwait(false);
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

        return new ResetRootResult
        {
            Root = root,
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

    private async Task<ResetPrsResult> RunPrsAsync(int root, bool execute, string comment, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetPrs(root, execute, comment, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetPrsResult)
            ?? throw new InvalidOperationException("reset prs returned unparseable JSON");
    }

    private async Task<ResetWorktreesResult> RunWorktreesAsync(int root, bool execute, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetWorktrees(root, execute, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetWorktreesResult)
            ?? throw new InvalidOperationException("reset worktrees returned unparseable JSON");
    }

    private async Task<ResetBranchesResult> RunBranchesAsync(int root, bool execute, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetBranches(root, execute, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetBranchesResult)
            ?? throw new InvalidOperationException("reset branches returned unparseable JSON");
    }

    private async Task<ResetFacetsResult> RunFacetsAsync(int root, bool execute, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetFacets(root, execute, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetFacetsResult)
            ?? throw new InvalidOperationException("reset facets returned unparseable JSON");
    }

    private async Task<ResetManifestResult> RunManifestAsync(int root, bool execute, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetManifest(root, execute, ct)).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, PolyphonyJsonContext.Default.ResetManifestResult)
            ?? throw new InvalidOperationException("reset manifest returned unparseable JSON");
    }

    private async Task<ResetStateResult> RunStateAsync(int root, bool execute, CancellationToken ct)
    {
        var json = await CaptureAsync(() => ResetState(root, execute, ct)).ConfigureAwait(false);
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
