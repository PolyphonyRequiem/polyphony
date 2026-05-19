using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony reset apex --apex N [--execute] [--skip-state] [--comment "..."]</c> —
/// composite that runs the full reset chain in the documented order:
/// <c>prs → worktrees → branches → facets → manifest → state</c>.
///
/// <para>Per <c>docs/decisions/run-reset.md</c>, the state stamp lands
/// LAST so that a crash partway through the cleanup leaves the system
/// "still mid-reset" (watermark unchanged → observers still see the
/// old satisfaction signals as "current run") rather than "watermark
/// advanced but PRs/branches still leak past it".</para>
///
/// <para><b>Step failure handling</b>: each step is run in turn. A step
/// reporting <see cref="ResetStateResult.Success"/> = false (or its
/// per-verb equivalent) becomes an entry in
/// <see cref="ResetApexResult.StepsFailed"/> and HALTS the chain — we
/// don't want to advance the watermark on top of a half-done cleanup.
/// Per-item failures inside a step (e.g. one PR that the platform
/// refused to close) do NOT halt the chain; they propagate through
/// the per-step result.</para>
///
/// <para><b>--skip-state</b>: useful when an operator wants to do the
/// cleanup pass without flipping the watermark — e.g. a one-off
/// hygiene sweep after a partial run that the operator believes
/// already advanced the watermark. Sets
/// <see cref="ResetApexResult.StateSkipped"/> = true and omits the
/// State step from <see cref="ResetApexResult.StepsCompleted"/>.</para>
///
/// <para><b>Dry-run propagation</b>: <c>--execute</c> (or its absence)
/// is propagated unchanged to every leg. A dry-run composite invokes
/// every sub-verb in dry-run mode so the operator sees a complete
/// preview of the chain's would-be effects in one envelope.</para>
/// </summary>
public sealed partial class ResetCommands
{
    /// <summary>
    /// Run the full reset chain for an apex.
    /// </summary>
    /// <param name="apex">Apex root work-item ID.</param>
    /// <param name="execute">Pass to actually execute each step. Without this flag, the verb is dry-run end-to-end.</param>
    /// <param name="skipState">Skip the final state-watermark stamp. Use when the cleanup sweep should not advance the per-apex run watermark.</param>
    /// <param name="comment">Optional override for the closing comment posted on each PR. See <see cref="ResetPrs"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("apex")]
    [VerbResult(typeof(ResetApexResult))]
    public async Task<int> ResetApex(
        int apex = RequiredInput.MissingInt,
        bool execute = false,
        bool skipState = false,
        string comment = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reset apex",
            ("--apex", apex == RequiredInput.MissingInt)) is { } halt)
            return halt;

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
            // 1) PRs
            prs = await RunPrsAsync(apex, execute, comment, ct).ConfigureAwait(false);
            if (prs.Success) stepsCompleted.Add("prs");
            else { stepsFailed.Add("prs"); haltReason = $"prs: {prs.Error}"; }

            // 2) Worktrees (only if prs succeeded — otherwise we don't
            //    know whether observers are quiet)
            if (haltReason is null)
            {
                worktrees = await RunWorktreesAsync(apex, execute, ct).ConfigureAwait(false);
                if (worktrees.Success) stepsCompleted.Add("worktrees");
                else { stepsFailed.Add("worktrees"); haltReason = $"worktrees: {worktrees.Error}"; }
            }

            // 3) Branches
            if (haltReason is null)
            {
                branches = await RunBranchesAsync(apex, execute, ct).ConfigureAwait(false);
                if (branches.Success) stepsCompleted.Add("branches");
                else { stepsFailed.Add("branches"); haltReason = $"branches: {branches.Error}"; }
            }

            // 4) Facets — strip polyphony:facets=* and polyphony:planned
            //    tags from the apex subtree. Must come AFTER branches
            //    (no point cleaning tags if the plan branch we're
            //    invalidating is still there) and BEFORE state (the
            //    watermark stamp invariant is "world is clean"; persisted
            //    planning decisions are part of "world").
            if (haltReason is null)
            {
                facets = await RunFacetsAsync(apex, execute, ct).ConfigureAwait(false);
                if (facets.Success) stepsCompleted.Add("facets");
                else { stepsFailed.Add("facets"); haltReason = $"facets: {facets.Error}"; }
            }

            // 5) Manifest (read-only inspection)
            if (haltReason is null)
            {
                manifest = await RunManifestAsync(apex, execute, ct).ConfigureAwait(false);
                if (manifest.Success) stepsCompleted.Add("manifest");
                else { stepsFailed.Add("manifest"); haltReason = $"manifest: {manifest.Error}"; }
            }

            // 6) State (last — only on a fully-clean chain, and only
            //    when not explicitly skipped)
            if (haltReason is null && !skipState)
            {
                state = await RunStateAsync(apex, execute, ct).ConfigureAwait(false);
                if (state.Success) stepsCompleted.Add("state");
                else { stepsFailed.Add("state"); haltReason = $"state: {state.Error}"; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            haltReason = $"composite threw: {ex.Message}";
        }

        var result = new ResetApexResult
        {
            Apex = apex,
            Success = stepsFailed.Count == 0 && haltReason is null,
            DryRun = !execute,
            StepsCompleted = stepsCompleted,
            StepsFailed = stepsFailed,
            Prs = prs,
            Worktrees = worktrees,
            Branches = branches,
            Facets = facets,
            Manifest = manifest,
            State = state,
            StateSkipped = skipState,
            Error = haltReason,
        };

        Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.ResetApexResult));
        return ExitCodes.Success;
    }

    // ---- internal runners ----------------------------------------------
    // The public [Command] methods write to Console as their last step;
    // for composite use we need the raw result record. Each runner
    // captures stdout from the public method, parses it back, and returns
    // the typed result. This keeps the per-verb dry-run/execute envelopes
    // identical between standalone and composite invocations.
    //
    // The capture-and-reparse pattern is deliberate: it ensures the
    // composite would observe exactly the same JSON shape an operator
    // would see when running the standalone verb. If we ever refactor a
    // sub-verb's envelope, the composite picks up the change for free.

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

    /// <summary>
    /// Capture the stdout written by an inner verb call. The verbs
    /// emit a single JSON line via <see cref="Console.WriteLine(string?)"/>,
    /// so we swap in a <see cref="StringWriter"/>, run the verb, and
    /// return the trimmed payload.
    ///
    /// <para>The wrapping is intentionally narrow — we capture only the
    /// verb's own emit, never anything written by ambient code, because
    /// no other code writes between the swap and the restore.</para>
    /// </summary>
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
