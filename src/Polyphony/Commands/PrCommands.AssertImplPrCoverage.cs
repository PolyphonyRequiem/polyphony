using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Verify that the squash-merge on the MG branch carries the full
    /// cumulative diff of the source impl branch. Defends against
    /// AB#3211 — silent commit drop on squash merge.
    ///
    /// Method: hash the cumulative diff of the impl branch from the MG
    /// branch's parent commit, then hash the diff of the MG branch's
    /// most recent commit (the squash). If hashes diverge, the squash
    /// did not carry what the impl branch had — surfaces the impl-branch
    /// commit list so an operator can identify which commits were dropped.
    ///
    /// Read-only: no refs are pushed, no merges are made, no state is
    /// mutated. The verb assumes refs have been freshly fetched
    /// upstream (the workflow's standard pre-step) and that the impl
    /// branch has not yet been deleted (the workflow passes
    /// <c>--delete-branch false</c> to <c>merge-impl-pr</c> when this
    /// assertion is in the chain).
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's apex (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the task whose impl PR was just squash-merged.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path of the enclosing MG (e.g. <c>pg-3176</c>).</param>
    /// <param name="remote">Remote name; both impl and mg refs are read as <c>{remote}/...</c>. Defaults to <c>origin</c>.</param>
    /// <param name="platform">Platform override (forward-compat; not consumed — coverage is platform-neutral, derived from local refs).</param>
    /// <param name="organization">ADO organization (forward-compat; not consumed).</param>
    /// <param name="project">ADO project (forward-compat; not consumed).</param>
    /// <param name="repositoryOverride">Repository override (forward-compat; not consumed).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("assert-impl-pr-coverage")]
    [VerbResult(typeof(PrAssertImplPrCoverageResult))]
    public async Task<int> AssertImplPrCoverage(
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        string mgPath = "",
        string remote = "origin",
        string platform = "",
        string organization = "",
        string project = "",
        string repositoryOverride = "",
        CancellationToken ct = default)
    {
        // The override flags are accepted for cross-platform workflow
        // symmetry: cascade-remedy.yaml + implement-merge-group.yaml
        // thread platform/org/project/repo through every PR-side verb,
        // and we want this verb to accept the same vocabulary even
        // though it operates exclusively on local git refs (no PR API
        // calls). Suppressed-unused — explicit discard for clarity.
        _ = platform;
        _ = organization;
        _ = project;
        _ = repositoryOverride;

        if (RequiredInput.HaltIfMissing("pr assert-impl-pr-coverage",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitCoverageError(rootId, itemId, mgPath, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }
        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitCoverageError(rootId, itemId, mgPath, $"itemId must be positive (got {itemId})");
            return ExitCodes.ConfigError;
        }
        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitCoverageError(rootId, itemId, mgPath, $"'{mgPath}' is not a valid merge-group path");
            return ExitCodes.ConfigError;
        }

        var implBranch = BranchNameBuilder.Impl(root, item).Value;
        var mgBranch = BranchNameBuilder.MergeGroup(root, path).Value;
        var implRef = $"{remote}/{implBranch}";
        var mgRef = $"{remote}/{mgBranch}";
        // Squash merge advances mg by exactly one commit; that commit's
        // diff is what we compare against the impl branch's cumulative
        // diff. The squash's parent {mgRef}^ is the state of the MG
        // branch immediately before the squash landed.
        var comparisonBase = $"{mgRef}^";

        try
        {
            var expectedDiff = await git.DiffAsync(comparisonBase, implRef, threeDot: false, ct).ConfigureAwait(false);
            var actualDiff = await git.DiffAsync(comparisonBase, mgRef, threeDot: false, ct).ConfigureAwait(false);

            var expectedHash = HashDiff(expectedDiff);
            var actualHash = HashDiff(actualDiff);
            var matches = string.Equals(expectedHash, actualHash, StringComparison.Ordinal);

            // Always enumerate impl-branch commits — they're cheap and
            // they make the gate prompt actionable when we mismatch.
            var commits = await git.RevListWithSubjectsAsync(
                $"{comparisonBase}..{implRef}", ct).ConfigureAwait(false);

            EmitCoverage(new PrAssertImplPrCoverageResult
            {
                Action = matches ? "ok" : "mismatch",
                RootId = rootId,
                ItemId = itemId,
                MgPath = mgPath,
                ImplRef = implRef,
                MgRef = mgRef,
                ComparisonBase = comparisonBase,
                ExpectedDiffHash = expectedHash,
                ActualDiffHash = actualHash,
                ExpectedDiffBytes = Encoding.UTF8.GetByteCount(expectedDiff),
                ActualDiffBytes = Encoding.UTF8.GetByteCount(actualDiff),
                ImplBranchCommits = [.. commits.Select(c => new PrAssertImplPrCoverageCommit
                {
                    Sha = c.Sha,
                    Subject = c.Subject,
                })],
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitCoverageError(rootId, itemId, mgPath, ex.Message, implRef, mgRef, comparisonBase);
            return ExitCodes.RoutingFailure;
        }
    }

    private static string HashDiff(string diff)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(diff));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void EmitCoverage(PrAssertImplPrCoverageResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrAssertImplPrCoverageResult));

    private static void EmitCoverageError(
        int rootId,
        int itemId,
        string mgPath,
        string message,
        string implRef = "",
        string mgRef = "",
        string comparisonBase = "")
    {
        EmitCoverage(new PrAssertImplPrCoverageResult
        {
            Action = "error",
            RootId = rootId,
            ItemId = itemId,
            MgPath = mgPath,
            ImplRef = implRef,
            MgRef = mgRef,
            ComparisonBase = comparisonBase,
            ExpectedDiffHash = string.Empty,
            ActualDiffHash = string.Empty,
            ExpectedDiffBytes = 0,
            ActualDiffBytes = 0,
            ImplBranchCommits = [],
            Error = message,
        });
    }
}
