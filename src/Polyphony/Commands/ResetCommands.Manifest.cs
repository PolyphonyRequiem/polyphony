using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony reset manifest --apex N [--execute]</c> — read-only
/// inspection of the run manifest at <c>origin/feature/{N}:.polyphony/run.yaml</c>.
///
/// <para><b>Why read-only</b>: the manifest is co-located with the
/// feature branch. <see cref="ResetBranches"/> deletes
/// <c>feature/{N}</c>, which clears the manifest as a side-effect.
/// There's no second copy of the manifest that needs separate cleanup.
/// This verb exists in PR 2 as an explicit inspection step so the
/// composite (<see cref="ResetApex"/>) can surface the manifest state
/// to the operator and so the workflow can route on it; <c>--execute</c>
/// is accepted (and ignored) for symmetry with the other reset verbs.</para>
///
/// <para>If a future PR introduces a partial-reset mode that preserves
/// <c>feature/{N}</c> while clearing the manifest in-place, this verb
/// is where that writer lands. For now it reports
/// <see cref="ResetManifestResult.DeferralReason"/> documenting why.</para>
///
/// <para><b>Failure tolerance</b>: a missing feature branch is reported
/// (not an error), a present branch with a missing manifest blob is
/// reported (not an error). Only an unexpected git failure marks
/// <see cref="ResetManifestResult.Success"/> = false.</para>
/// </summary>
public sealed partial class ResetCommands
{
    internal const string ManifestPath = ".polyphony/run.yaml";

    /// <summary>
    /// Inspect the run manifest for the apex.
    /// </summary>
    /// <param name="apex">Apex root work-item ID. Manifest path is <c>origin/feature/{apex}:.polyphony/run.yaml</c>.</param>
    /// <param name="execute">Accepted for symmetry; this verb is read-only in PR 2 and ignores the flag.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("manifest")]
    [VerbResult(typeof(ResetManifestResult))]
    public async Task<int> ResetManifest(
        int apex = RequiredInput.MissingInt,
        bool execute = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reset manifest",
            ("--apex", apex == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var featureBranch = $"feature/{apex}";

        ResetManifestResult result;
        try
        {
            var heads = await _git.LsRemoteHeadsAsync(
                "origin", $"refs/heads/{featureBranch}", ct).ConfigureAwait(false);
            var branchExists = heads.Any(line =>
                line.Contains($"refs/heads/{featureBranch}", StringComparison.Ordinal));

            bool manifestPresent = false;
            if (branchExists)
            {
                // ShowFileAtRefAsync returns null when the path doesn't
                // exist at the ref. We need to fetch the ref first to
                // ensure origin/feature/{N} is locally resolvable.
                try
                {
                    await _git.FetchAsync("origin", $"refs/heads/{featureBranch}:refs/remotes/origin/{featureBranch}", ct)
                        .ConfigureAwait(false);
                }
                catch (ExternalToolException)
                {
                    // Fetch failure is non-fatal — fall through and let
                    // ShowFileAtRefAsync report null on the next step.
                }

                var blob = await _git.ShowFileAtRefAsync(
                    $"origin/{featureBranch}", ManifestPath, ct).ConfigureAwait(false);
                manifestPresent = blob is not null;
            }

            result = new ResetManifestResult
            {
                Apex = apex,
                Success = true,
                DryRun = !execute,
                FeatureBranch = featureBranch,
                ManifestPath = ManifestPath,
                FeatureBranchExists = branchExists,
                ManifestPresent = manifestPresent,
                DeferralReason = branchExists
                    ? "manifest cleared as a side-effect of `polyphony reset branches` " +
                      $"(deletes {featureBranch})"
                    : "feature branch already absent; manifest has nothing to clear",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new ResetManifestResult
            {
                Apex = apex,
                Success = false,
                DryRun = !execute,
                FeatureBranch = featureBranch,
                ManifestPath = ManifestPath,
                FeatureBranchExists = false,
                ManifestPresent = false,
                Error = $"Error inspecting manifest for apex #{apex}: {ex.Message}",
            };
        }

        Emit(result);
        return ExitCodes.Success;
    }

    private static void Emit(ResetManifestResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result,
            PolyphonyJsonContext.Default.ResetManifestResult));
}
