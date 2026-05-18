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
    /// Merge a merge-group PR into its parent on Azure DevOps. ADO analogue
    /// of <c>polyphony pr merge-mg-pr</c>. Identifies the PR by its
    /// (head, base) pair: head is <c>mg/{root_id}_{mg_path}</c>; base is the
    /// parent merge-group branch when nested, or the feature branch when
    /// top-level.
    ///
    /// <para>The merge strategy is hardcoded to <c>noFastForward</c> by
    /// <see cref="IAdoClient.CompletePullRequestAsync"/> per ADR
    /// <c>docs/decisions/branch-model.md</c> — nested merge groups depend
    /// on git ancestry to know what is integrated; squash and rebase would
    /// break the chain. The head branch is never deleted (sibling merge
    /// groups may still be in flight); <c>completionOptions.deleteSourceBranch</c>
    /// is pinned to <c>false</c>.</para>
    ///
    /// <para><b>No <c>--admin</c> flag</b>: ADO bypasses branch-protection
    /// policies via <c>completionOptions.bypassPolicy</c>, which is pinned
    /// to <c>false</c> in the current
    /// <see cref="AdoCompletionOptions"/> shape. Exposing a CLI bypass flag
    /// is deferred — same deferral as #104 (<c>merge-plan-ado</c>).</para>
    ///
    /// <para><b>Routing-style exit code</b> — always exits 0; consumers
    /// branch on <see cref="PrMergeMergeGroupAdoResult.ErrorCode"/>.</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name; both accepted.</param>
    /// <param name="rootId">Root work-item id of the run's apex (focus) item.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path being merged.</param>
    /// <param name="matchHeadCommit">
    /// When set, the verb refuses to merge if the polled MG-branch SHA does
    /// not match this value. Use to guard against races between status checks
    /// and merge. The same SHA is also forwarded as ADO's
    /// <c>lastMergeSourceCommit.commitId</c> stale-head guard. When omitted,
    /// the polled head SHA is used directly (no pre-check, but ADO still
    /// guards against a source-branch advance between poll and complete).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("merge-mg-ado")]
    [VerbResult(typeof(PrMergeMergeGroupAdoResult))]
    public async Task<int> MergeMergeGroupAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int rootId = RequiredInput.MissingInt,
        string mgPath = "",
        string matchHeadCommit = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr merge-mg-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        const string MgMethod = "merge";
        const bool MgDeleteBranch = false;

        var slug = BuildAdoSlug(organization, project, repository);

        // ── 1. Validate inputs. ────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository))
        {
            EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "invalid_argument", "organization, project, and repository are required");
            return ExitCodes.Success;
        }
        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "invalid_argument", $"rootId must be positive (got {rootId})");
            return ExitCodes.Success;
        }
        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "invalid_argument",
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.Success;
        }

        var headBranch = BranchNameBuilder.MergeGroup(root, path).Value;
        var baseBranch = path.IsTopLevel
            ? BranchNameBuilder.Feature(root).Value
            : BranchNameBuilder.MergeGroup(root, MergeGroupPath.Of(path.Segments.Take(path.Depth - 1))).Value;
        var canonicalMgPath = path.Canonical;

        if (ado is null)
        {
            EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "ado_failed", "IAdoClient is not configured", headBranch, baseBranch);
            return ExitCodes.Success;
        }

        try
        {
            // ── 2. Locate the PR by (source, target) pair. ────────────────
            var activePrs = await ado.ListPullRequestsAsync(
                organization, project, repository,
                AdoPullRequestStatus.All, null, ct).ConfigureAwait(false);

            if (activePrs is null)
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            var expectedSourceRef = "refs/heads/" + headBranch;
            var expectedTargetRef = "refs/heads/" + baseBranch;
            AdoPullRequest? activePr = null;
            AdoPullRequest? completedPr = null;
            foreach (var pr in activePrs)
            {
                if (!string.Equals(pr.SourceRefName, expectedSourceRef, StringComparison.Ordinal)) continue;
                if (!string.Equals(pr.TargetRefName, expectedTargetRef, StringComparison.Ordinal)) continue;

                if (string.Equals(pr.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    activePr = pr;
                    break;
                }
                if (string.Equals(pr.Status, "completed", StringComparison.OrdinalIgnoreCase)
                    && (completedPr is null || pr.CreationDate < completedPr.CreationDate))
                {
                    // Prefer the OLDEST completed match: when a previous run
                    // produced a real merge and a subsequent retry opened a
                    // no-op duplicate (AB#3228 symptom), the older PR is the
                    // one with the populated merge commit. The newer phantom
                    // would re-trip `missing_merge_commit` here.
                    completedPr = pr;
                }
            }

            // Already-merged path: an existing completed PR matches the pair.
            if (activePr is null && completedPr is not null)
            {
                var prUrlMerged = !string.IsNullOrEmpty(completedPr.Url)
                    ? completedPr.Url
                    : BuildAdoPrUrl(organization, project, repository, completedPr.PullRequestId);

                // Read the merge commit from the poll-data composer (the list
                // endpoint does not surface lastMergeCommit). Best-effort —
                // if it fails, we still report the PR as already-merged but
                // without a SHA, and the workflow can route on
                // missing_merge_commit.
                string? mergeSha = null;
                try
                {
                    var pollMerged = await ado.GetPullRequestPollDataAsync(
                        organization, project, repository, completedPr.PullRequestId, ct).ConfigureAwait(false);
                    mergeSha = pollMerged?.MergeCommit;
                }
                catch (Exception)
                {
                    // best-effort
                }

                if (string.IsNullOrEmpty(mergeSha))
                {
                    EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                        "missing_merge_commit",
                        $"PR #{completedPr.PullRequestId} reports state completed but ADO did not return a merge commit SHA.",
                        headBranch, baseBranch,
                        prNumber: completedPr.PullRequestId, prUrl: prUrlMerged, prState: "MERGED",
                        merged: true, alreadyMerged: true, mergeCommit: "");
                    return ExitCodes.Success;
                }

                EmitMergeMgAdo(new PrMergeMergeGroupAdoResult
                {
                    RootId = rootId,
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
                    Method = MgMethod,
                    Merged = true,
                    AlreadyMerged = true,
                    DeleteBranch = MgDeleteBranch,
                    MergeCommit = mergeSha,
                    ErrorCode = "",
                });
                return ExitCodes.Success;
            }

            if (activePr is null)
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "pr_not_found",
                    $"No active PR found in {slug} for head='{headBranch}' base='{baseBranch}'.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            var prUrl = !string.IsNullOrEmpty(activePr.Url)
                ? activePr.Url
                : BuildAdoPrUrl(organization, project, repository, activePr.PullRequestId);

            // ── 3. Poll for the current head SHA + state. ─────────────────
            // Identity (source/target ref) is already verified above; we
            // re-poll to read the live HeadRefOid for the stale-head guard
            // and to catch a state change between list and complete.
            AdoPullRequestPollData? poll;
            try
            {
                poll = await ado.GetPullRequestPollDataAsync(
                    organization, project, repository, activePr.PullRequestId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex)
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "no_pat", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (TimeoutException ex)
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "ado_timeout", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (HttpRequestException ex)
            {
                var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "no_pat"
                    : "ado_failed";
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    code, ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "ado_failed",
                    $"ADO PR poll failed: {ex.Message}",
                    headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }

            if (poll is null)
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "pr_not_found",
                    $"PR #{activePr.PullRequestId} disappeared between list and poll in {slug}.",
                    headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }

            // ── 3b. Branch on PR state from the live poll. ────────────────
            if (string.Equals(poll.State, "MERGED", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(poll.MergeCommit))
                {
                    EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                        "missing_merge_commit",
                        $"PR #{activePr.PullRequestId} reports state MERGED but ADO did not return a merge commit SHA.",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State,
                        merged: true, alreadyMerged: true);
                    return ExitCodes.Success;
                }

                EmitMergeMgAdo(new PrMergeMergeGroupAdoResult
                {
                    RootId = rootId,
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
                    Method = MgMethod,
                    Merged = true,
                    AlreadyMerged = true,
                    DeleteBranch = MgDeleteBranch,
                    MergeCommit = poll.MergeCommit,
                    ErrorCode = "",
                });
                return ExitCodes.Success;
            }

            if (!string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "pr_state_invalid",
                    $"PR #{activePr.PullRequestId} is in state '{poll.State}'; only OPEN or MERGED are actionable.",
                    headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }

            // ── 3c. Stale-head pre-check (when --match-head-commit is set). ─
            if (!string.IsNullOrEmpty(matchHeadCommit)
                && !string.Equals(matchHeadCommit, poll.HeadRefOid, StringComparison.OrdinalIgnoreCase))
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "stale_head",
                    $"--match-head-commit '{matchHeadCommit}' does not match the polled head SHA '{poll.HeadRefOid}' for PR #{activePr.PullRequestId}. Re-poll and retry.",
                    headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }

            // ── 4. Complete the PR. ────────────────────────────────────────
            // Use the polled head SHA as ADO's lastMergeSourceCommit guard so
            // a push between poll and complete still surfaces as stale_head.
            var lastMergeSha = poll.HeadRefOid;

            AdoCompletePullRequestResult complete;
            try
            {
                complete = await ado.CompletePullRequestAsync(
                    organization, project, repository, activePr.PullRequestId,
                    lastMergeSourceCommitSha: lastMergeSha,
                    mergeStrategy: AdoMergeStrategy.NoFastForward,
                    deleteSourceBranch: false,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex)
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "no_pat", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }
            catch (TimeoutException ex)
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "ado_timeout", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }
            catch (HttpRequestException ex)
            {
                var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "no_pat"
                    : "ado_complete_failed";
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    code, ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
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
                        EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                            "missing_merge_commit",
                            "ADO complete-PR succeeded but did not return a merge commit SHA.",
                            headBranch, baseBranch,
                            prNumber: activePr.PullRequestId, prUrl: prUrl, prState: "MERGED",
                            merged: true, alreadyMerged: false);
                        return ExitCodes.Success;
                    }
                    EmitMergeMgAdo(new PrMergeMergeGroupAdoResult
                    {
                        RootId = rootId,
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
                        Method = MgMethod,
                        Merged = true,
                        AlreadyMerged = false,
                        DeleteBranch = MgDeleteBranch,
                        MergeCommit = complete.MergeCommitSha,
                        ErrorCode = "",
                    });
                    return ExitCodes.Success;

                case "stale_head":
                    EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                        "stale_head",
                        $"ADO refused to complete PR #{activePr.PullRequestId}: source branch advanced past the polled head SHA '{lastMergeSha}'. Re-poll and retry. Detail: {complete.ErrorBody}",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                    return ExitCodes.Success;

                case "not_found":
                    EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                        "pr_not_found",
                        $"PR #{activePr.PullRequestId} disappeared between poll and complete in {slug}.",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                    return ExitCodes.Success;

                case "not_mergeable":
                default:
                    EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
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
            EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "no_pat", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "ado_timeout", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                code, ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitMergeMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "ado_failed", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
    }

    private static void EmitMergeMgAdo(PrMergeMergeGroupAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrMergeMergeGroupAdoResult));

    private static void EmitMergeMgAdoError(
        int rootId,
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
        EmitMergeMgAdo(new PrMergeMergeGroupAdoResult
        {
            RootId = rootId,
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
            Method = "merge",
            Merged = merged,
            AlreadyMerged = alreadyMerged,
            DeleteBranch = false,
            MergeCommit = mergeCommit,
            ErrorCode = errorCode,
            Error = message,
        });
    }
}
