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
    /// Merge a feature PR into its target branch on Azure DevOps. ADO analogue
    /// of the GitHub <c>gh pr merge --squash</c> path used by
    /// <c>github-pr.yaml</c> for feature PRs. Identifies the PR by its
    /// (head, base) pair: head is <c>feature/{root_id}</c>; base is the
    /// configured target branch (default <c>main</c>).
    ///
    /// <para>The merge strategy is hardcoded to <c>noFastForward</c> by
    /// <see cref="IAdoClient.CompletePullRequestAsync"/>. GitHub's flow
    /// squashes — see <see cref="PrMergeFeatureAdoResult"/> for why this
    /// divergence is intentional and deferred. The head branch is never
    /// deleted (polyphony's run manifest lives at
    /// <c>feature/{root}:.polyphony/run.yaml</c>);
    /// <c>completionOptions.deleteSourceBranch</c> is pinned to <c>false</c>.</para>
    ///
    /// <para><b>No <c>--admin</c> flag</b>: ADO bypasses branch-protection
    /// policies via <c>completionOptions.bypassPolicy</c>, which is pinned
    /// to <c>false</c> in the current
    /// <see cref="AdoCompletionOptions"/> shape. Exposing a CLI bypass flag
    /// is deferred — same deferral as #104 (<c>merge-plan-ado</c>) and #106
    /// (<c>merge-mg-ado</c>).</para>
    ///
    /// <para><b>Routing-style exit code</b> — always exits 0; consumers
    /// branch on <see cref="PrMergeFeatureAdoResult.ErrorCode"/>.</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name; both accepted.</param>
    /// <param name="rootId">Root work-item id of the run's apex (focus) item.</param>
    /// <param name="targetBranch">Target branch (typically <c>main</c>); defaults to <c>main</c>.</param>
    /// <param name="matchHeadCommit">
    /// When set, the verb refuses to merge if the polled feature-branch SHA
    /// does not match this value. Use to guard against races between status
    /// checks and merge. The same SHA is also forwarded as ADO's
    /// <c>lastMergeSourceCommit.commitId</c> stale-head guard. When omitted,
    /// the polled head SHA is used directly (no pre-check, but ADO still
    /// guards against a source-branch advance between poll and complete).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("merge-feature-ado")]
    [VerbResult(typeof(PrMergeFeatureAdoResult))]
    public async Task<int> MergeFeatureAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int rootId = RequiredInput.MissingInt,
        string targetBranch = "main",
        string matchHeadCommit = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr merge-feature-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--root-id", rootId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        const string FeatureMethod = "merge";
        const bool FeatureDeleteBranch = false;

        var slug = BuildAdoSlug(organization, project, repository);

        // ── 1. Validate inputs. ────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository))
        {
            EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                "invalid_argument", "organization, project, and repository are required");
            return ExitCodes.Success;
        }
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                "invalid_argument", "targetBranch is required");
            return ExitCodes.Success;
        }
        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                "invalid_argument", $"rootId must be positive (got {rootId})");
            return ExitCodes.Success;
        }

        var headBranch = BranchNameBuilder.Feature(root).Value;
        var baseBranch = targetBranch;

        if (ado is null)
        {
            EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
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
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
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
                if (string.Equals(pr.Status, "completed", StringComparison.OrdinalIgnoreCase) && completedPr is null)
                {
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
                    EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                        "missing_merge_commit",
                        $"PR #{completedPr.PullRequestId} reports state completed but ADO did not return a merge commit SHA.",
                        headBranch, baseBranch,
                        prNumber: completedPr.PullRequestId, prUrl: prUrlMerged, prState: "MERGED",
                        merged: true, alreadyMerged: true, mergeCommit: "");
                    return ExitCodes.Success;
                }

                EmitMergeFeatureAdo(new PrMergeFeatureAdoResult
                {
                    RootId = rootId,
                    HeadBranch = headBranch,
                    BaseBranch = baseBranch,
                    Organization = organization,
                    Project = project,
                    Repository = repository,
                    RepoSlug = slug,
                    PrNumber = completedPr.PullRequestId,
                    PrUrl = prUrlMerged,
                    PrState = "MERGED",
                    Method = FeatureMethod,
                    Merged = true,
                    AlreadyMerged = true,
                    DeleteBranch = FeatureDeleteBranch,
                    MergeCommit = mergeSha,
                    ErrorCode = "",
                });
                return ExitCodes.Success;
            }

            if (activePr is null)
            {
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
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
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                    "no_pat", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (TimeoutException ex)
            {
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                    "ado_timeout", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (HttpRequestException ex)
            {
                var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "no_pat"
                    : "ado_failed";
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                    code, ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                    "ado_failed",
                    $"ADO PR poll failed: {ex.Message}",
                    headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl);
                return ExitCodes.Success;
            }

            if (poll is null)
            {
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
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
                    EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                        "missing_merge_commit",
                        $"PR #{activePr.PullRequestId} reports state MERGED but ADO did not return a merge commit SHA.",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State,
                        merged: true, alreadyMerged: true);
                    return ExitCodes.Success;
                }

                EmitMergeFeatureAdo(new PrMergeFeatureAdoResult
                {
                    RootId = rootId,
                    HeadBranch = headBranch,
                    BaseBranch = baseBranch,
                    Organization = organization,
                    Project = project,
                    Repository = repository,
                    RepoSlug = slug,
                    PrNumber = activePr.PullRequestId,
                    PrUrl = prUrl,
                    PrState = poll.State,
                    Method = FeatureMethod,
                    Merged = true,
                    AlreadyMerged = true,
                    DeleteBranch = FeatureDeleteBranch,
                    MergeCommit = poll.MergeCommit,
                    ErrorCode = "",
                });
                return ExitCodes.Success;
            }

            if (!string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
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
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
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
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                    "no_pat", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }
            catch (TimeoutException ex)
            {
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                    "ado_timeout", ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }
            catch (HttpRequestException ex)
            {
                var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "no_pat"
                    : "ado_complete_failed";
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                    code, ex.Message, headBranch, baseBranch,
                    prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
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
                        EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                            "missing_merge_commit",
                            "ADO complete-PR succeeded but did not return a merge commit SHA.",
                            headBranch, baseBranch,
                            prNumber: activePr.PullRequestId, prUrl: prUrl, prState: "MERGED",
                            merged: true, alreadyMerged: false);
                        return ExitCodes.Success;
                    }
                    EmitMergeFeatureAdo(new PrMergeFeatureAdoResult
                    {
                        RootId = rootId,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        RepoSlug = slug,
                        PrNumber = activePr.PullRequestId,
                        PrUrl = prUrl,
                        PrState = "MERGED",
                        Method = FeatureMethod,
                        Merged = true,
                        AlreadyMerged = false,
                        DeleteBranch = FeatureDeleteBranch,
                        MergeCommit = complete.MergeCommitSha,
                        ErrorCode = "",
                    });
                    return ExitCodes.Success;

                case "stale_head":
                    EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                        "stale_head",
                        $"ADO refused to complete PR #{activePr.PullRequestId}: source branch advanced past the polled head SHA '{lastMergeSha}'. Re-poll and retry. Detail: {complete.ErrorBody}",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                    return ExitCodes.Success;

                case "not_found":
                    EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                        "pr_not_found",
                        $"PR #{activePr.PullRequestId} disappeared between poll and complete in {slug}.",
                        headBranch, baseBranch,
                        prNumber: activePr.PullRequestId, prUrl: prUrl, prState: poll.State);
                    return ExitCodes.Success;

                case "not_mergeable":
                default:
                    EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
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
            EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                "no_pat", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                "ado_timeout", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                code, ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitMergeFeatureAdoError(rootId, organization, project, repository, slug,
                "ado_failed", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
    }

    private static void EmitMergeFeatureAdo(PrMergeFeatureAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrMergeFeatureAdoResult));

    private static void EmitMergeFeatureAdoError(
        int rootId,
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
        EmitMergeFeatureAdo(new PrMergeFeatureAdoResult
        {
            RootId = rootId,
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
