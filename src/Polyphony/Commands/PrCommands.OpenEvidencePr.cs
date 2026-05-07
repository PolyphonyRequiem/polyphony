using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes an evidence branch
    /// into its parent feature branch (the run-root <c>feature/&lt;apex&gt;</c>
    /// trunk), or into <c>main</c> for the orphan-evidence case where no
    /// apex is supplied. Reuses an existing open PR for the same head/base
    /// pair instead of creating a duplicate.
    /// </summary>
    /// <remarks>
    /// Default branch naming follows Phase 6 PR #2 (the evidence branch
    /// builder):
    /// <list type="bullet">
    ///   <item>apex differs from work item (the normal case): head =
    ///     <c>evidence/&lt;apex&gt;-&lt;workItem&gt;</c>, base =
    ///     <c>feature/&lt;apex&gt;</c>.</item>
    ///   <item>apex omitted or equal to work item (orphan evidence): head =
    ///     <c>evidence/&lt;workItem&gt;</c>, base = <c>main</c>.</item>
    /// </list>
    /// Per-PR overrides via <c>--head</c> / <c>--base-branch</c> are honored
    /// verbatim. Title/body default to a deterministic stub composed from
    /// twig (the work item title); explicit overrides via <c>--title</c> /
    /// <c>--body</c> bypass twig entirely.
    /// </remarks>
    /// <param name="workItem">The actionable work-item id this evidence PR satisfies.</param>
    /// <param name="apexId">Optional run-root feature id. When omitted (or zero), defaults to <paramref name="workItem"/> (orphan evidence).</param>
    /// <param name="head">Optional head branch override. Defaults to the canonical evidence-branch name above.</param>
    /// <param name="baseBranch">Optional base branch override. Defaults to <c>feature/&lt;apex&gt;</c>, or <c>main</c> in the orphan case.</param>
    /// <param name="title">Optional PR title; deterministic fallback derived from the work item's twig title when empty.</param>
    /// <param name="body">Optional PR body; minimal placeholder stub used when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-evidence-pr")]
    public async Task<int> OpenEvidencePr(
        int workItem,
        int apexId = 0,
        string head = "",
        string baseBranch = "",
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (workItem <= 0)
        {
            EmitEvidenceError(workItem, apexId, $"workItem must be positive (got {workItem})");
            return ExitCodes.ConfigError;
        }

        if (apexId < 0)
        {
            EmitEvidenceError(workItem, apexId, $"apexId must be non-negative (got {apexId})");
            return ExitCodes.ConfigError;
        }

        // apexId omitted (zero) collapses to the orphan-evidence case where
        // the work item is its own apex. This mirrors PR #2's branch verb,
        // which uses the same convention to pick the simpler `evidence/<id>`
        // form.
        var effectiveApex = apexId == 0 ? workItem : apexId;
        var isOrphan = effectiveApex == workItem;

        var headBranch = string.IsNullOrWhiteSpace(head)
            ? (isOrphan
                ? $"evidence/{workItem}"
                : $"evidence/{effectiveApex}-{workItem}")
            : head;

        var resolvedBase = string.IsNullOrWhiteSpace(baseBranch)
            ? (isOrphan ? "main" : $"feature/{effectiveApex}")
            : baseBranch;

        try
        {
            // Validate both head and base exist on the remote — gh pr create
            // would otherwise fail late with a less actionable error. Mirrors
            // the open-mg-pr / open-impl-pr precondition contract.
            var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", ct).ConfigureAwait(false);
            if (headRefs.Count == 0)
            {
                EmitEvidenceError(
                    workItem, effectiveApex,
                    $"head branch '{headBranch}' does not exist on remote",
                    headBranch: headBranch, baseBranch: resolvedBase);
                return ExitCodes.RoutingFailure;
            }

            var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{resolvedBase}", ct).ConfigureAwait(false);
            if (baseRefs.Count == 0)
            {
                EmitEvidenceError(
                    workItem, effectiveApex,
                    $"base branch '{resolvedBase}' does not exist on remote",
                    headBranch: headBranch, baseBranch: resolvedBase);
                return ExitCodes.RoutingFailure;
            }

            var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(slug))
            {
                EmitEvidenceError(
                    workItem, effectiveApex,
                    "Could not resolve repo slug from origin remote",
                    headBranch: headBranch, baseBranch: resolvedBase);
                return ExitCodes.RoutingFailure;
            }

            var prTitle = string.IsNullOrWhiteSpace(title)
                ? await ResolveEvidencePrTitleAsync(workItem, ct).ConfigureAwait(false)
                : title;
            var prBody = string.IsNullOrWhiteSpace(body)
                ? BuildDefaultEvidenceBody(workItem, effectiveApex, headBranch, resolvedBase)
                : body;

            // Reuse an existing open PR for the same head/base pair instead
            // of creating a duplicate. Mirrors the create-feature-pr /
            // open-mg-pr / open-impl-pr resume idempotency contract.
            var existing = await gh.ListPullRequestsAsync(
                slug,
                new PrListFilters(Head: headBranch, Base: resolvedBase, State: "open", Limit: 1),
                ct).ConfigureAwait(false);
            if (existing.Count > 0)
            {
                var found = existing[0];
                EmitEvidence(new PrOpenEvidenceResult
                {
                    PrNumber = found.Number,
                    PrUrl = found.Url ?? "",
                    Title = prTitle,
                    HeadBranch = headBranch,
                    BaseBranch = resolvedBase,
                    WorkItemId = workItem,
                    ApexId = effectiveApex,
                    Created = false,
                });
                return ExitCodes.Success;
            }

            var url = await gh.CreatePullRequestAsync(slug, resolvedBase, headBranch, prTitle, prBody, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                EmitEvidenceError(
                    workItem, effectiveApex,
                    "gh pr create failed — no URL returned",
                    headBranch: headBranch, baseBranch: resolvedBase);
                return ExitCodes.RoutingFailure;
            }

            var trimmedUrl = url.Trim();
            EmitEvidence(new PrOpenEvidenceResult
            {
                PrNumber = ExtractPrNumber(trimmedUrl),
                PrUrl = trimmedUrl,
                Title = prTitle,
                HeadBranch = headBranch,
                BaseBranch = resolvedBase,
                WorkItemId = workItem,
                ApexId = effectiveApex,
                Created = true,
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitEvidenceError(
                workItem, effectiveApex, ex.Message,
                headBranch: headBranch, baseBranch: resolvedBase);
            return ExitCodes.RoutingFailure;
        }
    }

    private async Task<string> ResolveEvidencePrTitleAsync(int workItem, CancellationToken ct)
    {
        var fallback = $"Evidence for #{workItem}";
        try
        {
            var tree = await twig.ShowTreeAsync(workItem, ct).ConfigureAwait(false);
            var workItemTitle = tree?["title"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(workItemTitle)
                ? fallback
                : $"Evidence: {workItemTitle} (#{workItem})";
        }
        catch
        {
            return fallback;
        }
    }

    private static string BuildDefaultEvidenceBody(int workItem, int apexId, string headBranch, string baseBranch)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## Evidence for #").Append(workItem);
        if (apexId != workItem)
        {
            sb.Append(" (apex feature #").Append(apexId).Append(')');
        }
        sb.Append("\n\n");
        sb.Append("Promotes `").Append(headBranch).Append("` into `").Append(baseBranch).Append("`.\n\n");
        sb.Append("Work item: AB#").Append(workItem).Append("\n\n");
        sb.Append("### Evidence\n\n");
        sb.Append("<!-- Replace this stub with the artifacts that justify closing the work item.\n");
        sb.Append("     Free-form: links, notes, transcripts, screenshots, decision rationale.\n");
        sb.Append("     The plan reviewer judges sufficiency; there is no required schema. -->\n");
        return sb.ToString();
    }

    private static void EmitEvidence(PrOpenEvidenceResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenEvidenceResult));

    private static void EmitEvidenceError(
        int workItem,
        int apexId,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitEvidence(new PrOpenEvidenceResult
        {
            PrNumber = 0,
            PrUrl = "",
            Title = "",
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            WorkItemId = workItem,
            ApexId = apexId == 0 ? workItem : apexId,
            Created = false,
            Error = message,
        });
    }
}
