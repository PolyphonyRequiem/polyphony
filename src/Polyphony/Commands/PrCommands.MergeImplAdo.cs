using System.Net;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.AzureDevOps;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Merge the per-item impl PR into its enclosing merge-group branch on
    /// Azure DevOps. ADO analogue of <c>polyphony pr merge-impl-pr</c>.
    /// Identifies the PR by its (head, base) pair: head is
    /// <c>impl/{root_id}-{item_id}</c>, base is <c>mg/{root_id}_{mg_path}</c>.
    ///
    /// <para>Strategy is hardcoded to <c>squash</c> (impl PRs carry
    /// micro-history we do not want to pollute the merge-group branch with);
    /// the head branch is deleted after merge by default (impl branches are
    /// single-use), but the planner may override via
    /// <paramref name="deleteBranch"/>.</para>
    ///
    /// <para><b>Routing-style exit code</b> — always exits 0; consumers
    /// branch on <see cref="PrMergeImplAdoResult.ErrorCode"/>.</para>
    /// </summary>
    /// <param name="organization">ADO organization name.</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name.</param>
    /// <param name="rootId">Root work-item id of the run's apex (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the task.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path of the enclosing MG.</param>
    /// <param name="matchHeadCommit">
    /// When set, the verb refuses to merge if the polled impl-branch SHA does
    /// not match this value. Forwarded as ADO's <c>lastMergeSourceCommit</c>
    /// stale-head guard. When omitted, the polled head SHA is used directly.
    /// </param>
    /// <param name="deleteBranch">
    /// Delete the impl source branch on the ADO complete-PR call. Default
    /// <c>"true"</c>. Accepts <c>"true"</c> or <c>"false"</c>
    /// (case-insensitive) — declared as <see cref="string"/> rather than
    /// <see cref="bool"/> so workflow YAMLs can pass the explicit-value form;
    /// see <see cref="StringBoolArg"/> for the rationale.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("merge-impl-ado")]
    [VerbResult(typeof(PrMergeImplAdoResult))]
    public async Task<int> MergeImplAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        string mgPath = "",
        string matchHeadCommit = "",
        string deleteBranch = "true",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr merge-impl-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        var deleteBranchParsed = StringBoolArg.Parse("pr merge-impl-ado", "--delete-branch", deleteBranch);
        if (deleteBranchParsed is null) return ExitCodes.RoutingFailure;

        const string ImplMethod = "squash";
        var implDeleteBranch = deleteBranchParsed.Value;

        var slug = BuildAdoSlug(organization, project, repository);

        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "invalid_argument", $"rootId must be positive (got {rootId})");
            return ExitCodes.Success;
        }
        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "invalid_argument", $"itemId must be positive (got {itemId})");
            return ExitCodes.Success;
        }
        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "invalid_argument",
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.Success;
        }

        var headBranch = BranchNameBuilder.Impl(root, item).Value;
        var baseBranch = BranchNameBuilder.MergeGroup(root, path).Value;
        var canonicalMgPath = path.Canonical;

        if (ado is null)
        {
            EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "ado_failed", "IAdoClient is not configured", headBranch, baseBranch);
            return ExitCodes.Success;
        }

        try
        {
            // Locate the PR by source/target ref — filter by source branch
            // server-side, then match target ref locally.
            var allPrs = await ado.ListPullRequestsAsync(
                organization, project, repository,
                AdoPullRequestStatus.All, sourceBranch: headBranch, ct).ConfigureAwait(false);

            if (allPrs is null)
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            var expectedTargetRef = "refs/heads/" + baseBranch;
            AdoPullRequest? activePr = null;
            AdoPullRequest? completedPr = null;
            foreach (var pr in allPrs)
            {
                if (!string.Equals(pr.TargetRefName, expectedTargetRef, StringComparison.Ordinal)) continue;
                if (string.Equals(pr.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    activePr = pr;
                    break;
                }
                if (string.Equals(pr.Status, "completed", StringComparison.OrdinalIgnoreCase)
                    && (completedPr is null || pr.CreationDate < completedPr.CreationDate))
                {
                    // Prefer the OLDEST completed match (AB#3228): retries can
                    // accumulate no-op phantom PRs; the real merge is the
                    // first attempt.
                    completedPr = pr;
                }
            }

            // Already-merged path.
            if (activePr is null && completedPr is not null)
            {
                // Branch-recycle staleness check (AB#3211 root cause):
                // ADO never deletes PR records, so yesterday's completed
                // PR for the same branch pair surfaces here even when
                // today's branches contain entirely different work. The
                // helper rejects PRs whose recorded source SHA doesn't
                // match origin/{head} (or whose merge commit isn't on
                // origin/{base} when the head branch was deleted).
                var validity = await ValidateCompletedAdoPrAsync(
                    organization, project, repository,
                    completedPr.PullRequestId, headBranch, baseBranch, ct).ConfigureAwait(false);

                if (!validity.IsValid)
                {
                    // Stale completed PR — drop and fall through to the
                    // "no active PR" arm below.
                    completedPr = null;
                }
                else
                {
                    var prUrlMerged = !string.IsNullOrEmpty(completedPr.Url)
                        ? completedPr.Url
                        : BuildAdoPrUrl(organization, project, repository, completedPr.PullRequestId);
                    var mergeShaPrev = validity.MergeCommit;

                    if (string.IsNullOrEmpty(mergeShaPrev))
                    {
                        EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                            "missing_merge_commit",
                            $"PR #{completedPr.PullRequestId} reports state completed but ADO did not return a merge commit SHA.",
                            headBranch, baseBranch,
                            prNumber: completedPr.PullRequestId, prUrl: prUrlMerged, prState: "MERGED",
                            merged: true, alreadyMerged: true, mergeCommit: "");
                        return ExitCodes.Success;
                    }

                    EmitMergeImplAdo(new PrMergeImplAdoResult
                    {
                        RootId = rootId,
                        ItemId = itemId,
                        MgPath = canonicalMgPath,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        RepoSlug = slug,
                        PrNumber = completedPr.PullRequestId,
                        PrUrl = prUrlMerged,
                        PrState = "MERGED",
                        Method = ImplMethod,
                        Merged = true,
                        AlreadyMerged = true,
                        DeleteBranch = implDeleteBranch,
                        MergeCommit = mergeShaPrev,
                        ErrorCode = "",
                    });
                    return ExitCodes.Success;
                }
            }

            if (activePr is null)
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "pr_not_found",
                    $"No active PR found in {slug} for head='{headBranch}' base='{baseBranch}'.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            var prUrl = !string.IsNullOrEmpty(activePr.Url)
                ? activePr.Url
                : BuildAdoPrUrl(organization, project, repository, activePr.PullRequestId);

            // Poll for live HeadRefOid + state.
            AdoPullRequestPollData? poll;
            try
            {
                poll = await ado.GetPullRequestPollDataAsync(
                    organization, project, repository, activePr.PullRequestId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex)
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "no_pat", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (TimeoutException ex)
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "ado_timeout", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (HttpRequestException ex)
            {
                var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "no_pat"
                    : "ado_failed";
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    code, ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "ado_failed",
                    $"ADO PR poll failed: {ex.Message}",
                    headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }

            if (poll is null)
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "pr_not_found",
                    $"PR #{activePr.PullRequestId} disappeared between list and poll in {slug}.",
                    headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }

            if (string.Equals(poll.State, "MERGED", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(poll.MergeCommit))
                {
                    EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                        "missing_merge_commit",
                        $"PR #{activePr.PullRequestId} reports state MERGED but ADO did not return a merge commit SHA.",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State,
                        merged: true, alreadyMerged: true);
                    return ExitCodes.Success;
                }
                EmitMergeImplAdo(new PrMergeImplAdoResult
                {
                    RootId = rootId,
                    ItemId = itemId,
                    MgPath = canonicalMgPath,
                    HeadBranch = headBranch,
                    BaseBranch = baseBranch,
                    Organization = organization,
                    Project = project,
                    Repository = repository,
                    RepoSlug = slug,
                    PrNumber = activePr.PullRequestId,
                    PrUrl = prUrl,
                    PrState = poll.State,
                    Method = ImplMethod,
                    Merged = true,
                    AlreadyMerged = true,
                    DeleteBranch = implDeleteBranch,
                    MergeCommit = poll.MergeCommit,
                    ErrorCode = "",
                });
                return ExitCodes.Success;
            }

            if (!string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "pr_state_invalid",
                    $"PR #{activePr.PullRequestId} is in state '{poll.State}'; only OPEN or MERGED are actionable.",
                    headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }

            // Stale-head pre-check.
            if (!string.IsNullOrEmpty(matchHeadCommit)
                && !string.Equals(matchHeadCommit, poll.HeadRefOid, StringComparison.OrdinalIgnoreCase))
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "stale_head",
                    $"--match-head-commit '{matchHeadCommit}' does not match the polled head SHA '{poll.HeadRefOid}' for PR #{activePr.PullRequestId}. Re-poll and retry.",
                    headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }

            // Complete the PR via SQUASH; deleteSourceBranch honors the
            // verb's --delete-branch parameter (default true).
            var lastMergeSha = poll.HeadRefOid;
            AdoCompletePullRequestResult complete;
            try
            {
                complete = await ado.CompletePullRequestAsync(
                    organization, project, repository, activePr.PullRequestId,
                    lastMergeSourceCommitSha: lastMergeSha,
                    mergeStrategy: AdoMergeStrategy.Squash,
                    deleteSourceBranch: implDeleteBranch,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex)
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "no_pat", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }
            catch (TimeoutException ex)
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "ado_timeout", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }
            catch (HttpRequestException ex)
            {
                var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "no_pat"
                    : "ado_complete_failed";
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    code, ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "ado_complete_failed",
                    $"ADO complete-PR call failed: {ex.Message}",
                    headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }

            switch (complete.Status)
            {
                case "completed":
                    if (string.IsNullOrEmpty(complete.MergeCommitSha))
                    {
                        EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                            "missing_merge_commit",
                            "ADO complete-PR succeeded but did not return a merge commit SHA.",
                            headBranch, baseBranch,
                            prNumber: activePr.PullRequestId, prUrl: prUrl, prState: "MERGED",
                            merged: true, alreadyMerged: false);
                        return ExitCodes.Success;
                    }
                    EmitMergeImplAdo(new PrMergeImplAdoResult
                    {
                        RootId = rootId,
                        ItemId = itemId,
                        MgPath = canonicalMgPath,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        RepoSlug = slug,
                        PrNumber = activePr.PullRequestId,
                        PrUrl = prUrl,
                        PrState = "MERGED",
                        Method = ImplMethod,
                        Merged = true,
                        AlreadyMerged = false,
                        DeleteBranch = implDeleteBranch,
                        MergeCommit = complete.MergeCommitSha,
                        ErrorCode = "",
                    });
                    return ExitCodes.Success;

                case "stale_head":
                    EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                        "stale_head",
                        $"ADO refused to complete PR #{activePr.PullRequestId}: source branch advanced past the polled head SHA '{lastMergeSha}'. Re-poll and retry. Detail: {complete.ErrorBody}",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                    return ExitCodes.Success;

                case "not_found":
                    EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                        "pr_not_found",
                        $"PR #{activePr.PullRequestId} disappeared between poll and complete in {slug}.",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                    return ExitCodes.Success;

                case "completion_pending":
                    EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                        "completion_pending",
                        $"ADO accepted the complete-PR PATCH for PR #{activePr.PullRequestId} but the PR did not transition to status=completed within the poll budget. The merge may still land asynchronously, or the PR may be blocked by a policy. Inspect via `az repos pr show --id {activePr.PullRequestId}` and consider `az repos pr update --id {activePr.PullRequestId} --status completed` to land it manually. Detail: {complete.ErrorBody}",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                    return ExitCodes.Success;

                case "not_mergeable":
                default:
                    EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                        "ado_complete_failed",
                        $"ADO refused to complete PR #{activePr.PullRequestId} (HTTP {complete.HttpStatus}, status={complete.Status}): {complete.ErrorBody}",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                    return ExitCodes.Success;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "no_pat", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "ado_timeout", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                code, ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitMergeImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "ado_failed", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
    }

    private static void EmitMergeImplAdo(PrMergeImplAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrMergeImplAdoResult));

    private static void EmitMergeImplAdoError(
        int rootId,
        int itemId,
        string mgPath,
        string organization,
        string project,
        string repository,
        string slug,
        string errorCode,
        string message,
        string headBranch = "",
        string baseBranch = "",
        int prNumber = 0,
        string prUrl = "",
        string prState = "",
        bool merged = false,
        bool alreadyMerged = false,
        string mergeCommit = "")
    {
        EmitMergeImplAdo(new PrMergeImplAdoResult
        {
            RootId = rootId,
            ItemId = itemId,
            MgPath = mgPath ?? string.Empty,
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            Organization = organization ?? string.Empty,
            Project = project ?? string.Empty,
            Repository = repository ?? string.Empty,
            RepoSlug = slug ?? string.Empty,
            PrNumber = prNumber,
            PrUrl = prUrl,
            PrState = prState,
            Method = "squash",
            Merged = merged,
            AlreadyMerged = alreadyMerged,
            // Error envelopes echo the verb default (true). Callers that
            // need the requested value on success paths populate it directly.
            DeleteBranch = true,
            MergeCommit = mergeCommit,
            ErrorCode = errorCode,
            Error = message,
        });
    }
}
