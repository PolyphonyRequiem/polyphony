using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;

namespace Polyphony.Commands;

public sealed partial class BranchCommands
{
    /// <summary>
    /// Check whether a feature branch has zero commits ahead of the configured
    /// target branch. The target is read from
    /// <c>.polyphony-config/process-config.yaml → branch_strategy.target</c>
    /// (P5 — no hard-coded branch names). Both remote refs are fetched before
    /// the ancestry check to ensure the comparison is fresh.
    ///
    /// Used by <c>apex-driver.yaml</c> at preflight time to short-circuit
    /// re-dispatches whose prior changes are already in the target (AB#3175).
    /// </summary>
    /// <param name="feature">Feature branch name (e.g. feature/3165).</param>
    /// <param name="remote">Git remote name.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("check-zero-diff")]
    [VerbResult(typeof(BranchCheckZeroDiffResult))]
    public async Task<int> CheckZeroDiff(
        string feature = "",
        string remote = "origin",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("branch check-zero-diff",
            ("--feature", string.IsNullOrEmpty(feature))) is { } halt)
            return halt;

        var target = processConfig.BranchStrategy?.Target ?? "main";

        try
        {
            // Fetch both refs so the ancestry check reflects the current remote state.
            await git.FetchAsync(remote, target, ct).ConfigureAwait(false);
            await git.FetchAsync(remote, feature, ct).ConfigureAwait(false);

            // feature is zero-diff when it is an ancestor-or-equal of target —
            // i.e. target already contains every commit on feature.
            var isAncestor = await git.IsAncestorAsync(
                $"{remote}/{feature}",
                $"{remote}/{target}",
                ct).ConfigureAwait(false);

            var result = new BranchCheckZeroDiffResult
            {
                ZeroDiff = isAncestor,
                FeatureBranch = feature,
                TargetBranch = target,
            };
            Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.BranchCheckZeroDiffResult));
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var result = new BranchCheckZeroDiffResult
            {
                ZeroDiff = false,
                FeatureBranch = feature,
                TargetBranch = target,
                Error = ex.Message,
            };
            Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.BranchCheckZeroDiffResult));
            return ExitCodes.Success; // routing-style: always exit 0
        }
    }
}
