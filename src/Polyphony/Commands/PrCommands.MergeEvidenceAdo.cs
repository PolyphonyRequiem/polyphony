using System.Net;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.AzureDevOps;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Squash-merge an evidence PR on Azure DevOps. The ADO analogue of
    /// <c>polyphony pr merge-evidence-pr</c>. Identifies the PR by number
    /// (the caller — typically the actionable workflow — already knows it
    /// from the preceding <c>open-evidence-pr</c> step). Strategy is
    /// hardcoded to <c>squash</c> with <c>deleteSourceBranch=true</c>;
    /// evidence branches are single-use and never reused.
    ///
    /// <para><b>Routing-style exit code</b> — always exits 0; consumers
    /// branch on <see cref="PrMergeEvidenceAdoResult.ErrorCode"/>.</para>
    /// </summary>
    /// <param name="organization">ADO organization name.</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name.</param>
    /// <param name="prNumber">Evidence PR number to merge.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("merge-evidence-ado")]
    [VerbResult(typeof(PrMergeEvidenceAdoResult))]
    public async Task<int> MergeEvidenceAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int prNumber = RequiredInput.MissingInt,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr merge-evidence-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--pr-number", prNumber == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var slug = BuildAdoSlug(organization, project, repository);

        if (prNumber <= 0)
        {
            EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                "invalid_argument", $"prNumber must be positive (got {prNumber})");
            return ExitCodes.Success;
        }

        if (ado is null)
        {
            EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                "ado_failed", "IAdoClient is not configured");
            return ExitCodes.Success;
        }

        try
        {
            // Fetch the PR to surface URL + verify it exists. GetPullRequest
            // returns null for not-found; CompletePullRequest below relies
            // on the polled HeadRefOid for the stale-head guard.
            var pr = await ado.GetPullRequestAsync(
                organization, project, repository, prNumber, ct).ConfigureAwait(false);
            if (pr is null)
            {
                EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                    "pr_not_found",
                    $"PR #{prNumber} not found in {slug}.");
                return ExitCodes.Success;
            }

            var prUrl = !string.IsNullOrEmpty(pr.Url)
                ? pr.Url
                : BuildAdoPrUrl(organization, project, repository, prNumber);

            // Already-merged short-circuit.
            if (string.Equals(pr.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                string? mergeShaPrev = null;
                try
                {
                    var pollMerged = await ado.GetPullRequestPollDataAsync(
                        organization, project, repository, prNumber, ct).ConfigureAwait(false);
                    mergeShaPrev = pollMerged?.MergeCommit;
                }
                catch { /* best effort */ }

                EmitMergeEvidenceAdo(new PrMergeEvidenceAdoResult
                {
                    Organization = organization,
                    Project = project,
                    Repository = repository,
                    RepoSlug = slug,
                    PrNumber = prNumber,
                    PrUrl = prUrl,
                    Merged = true,
                    AlreadyMerged = true,
                    MergeCommit = mergeShaPrev ?? "",
                    ErrorCode = "",
                });
                return ExitCodes.Success;
            }

            // Poll for live HeadRefOid + state.
            AdoPullRequestPollData? poll;
            try
            {
                poll = await ado.GetPullRequestPollDataAsync(
                    organization, project, repository, prNumber, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "no_pat" : "ado_failed";
                EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                    code, ex.Message, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (TimeoutException ex)
            {
                EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                    "ado_timeout", ex.Message, prUrl: prUrl);
                return ExitCodes.Success;
            }

            if (poll is null)
            {
                EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                    "pr_not_found", $"PR #{prNumber} disappeared between get and poll in {slug}.",
                    prUrl: prUrl);
                return ExitCodes.Success;
            }

            if (string.Equals(poll.State, "MERGED", StringComparison.OrdinalIgnoreCase))
            {
                EmitMergeEvidenceAdo(new PrMergeEvidenceAdoResult
                {
                    Organization = organization,
                    Project = project,
                    Repository = repository,
                    RepoSlug = slug,
                    PrNumber = prNumber,
                    PrUrl = prUrl,
                    Merged = true,
                    AlreadyMerged = true,
                    MergeCommit = poll.MergeCommit ?? "",
                    ErrorCode = "",
                });
                return ExitCodes.Success;
            }

            if (!string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                    "not_mergeable",
                    $"PR #{prNumber} is in state '{poll.State}'; only OPEN or MERGED are actionable.",
                    prUrl: prUrl);
                return ExitCodes.Success;
            }

            var lastMergeSha = poll.HeadRefOid;
            AdoCompletePullRequestResult complete;
            try
            {
                complete = await ado.CompletePullRequestAsync(
                    organization, project, repository, prNumber,
                    lastMergeSourceCommitSha: lastMergeSha,
                    mergeStrategy: AdoMergeStrategy.Squash,
                    deleteSourceBranch: true,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "no_pat" : "ado_failed";
                EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                    code, ex.Message, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (TimeoutException ex)
            {
                EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                    "ado_timeout", ex.Message, prUrl: prUrl);
                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                    "ado_failed", $"ADO complete-PR call failed: {ex.Message}", prUrl: prUrl);
                return ExitCodes.Success;
            }

            switch (complete.Status)
            {
                case "completed":
                    if (string.IsNullOrEmpty(complete.MergeCommitSha))
                    {
                        EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                            "missing_merge_commit",
                            "ADO complete-PR succeeded but did not return a merge commit SHA.",
                            prUrl: prUrl, merged: true);
                        return ExitCodes.Success;
                    }
                    EmitMergeEvidenceAdo(new PrMergeEvidenceAdoResult
                    {
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        RepoSlug = slug,
                        PrNumber = prNumber,
                        PrUrl = prUrl,
                        Merged = true,
                        AlreadyMerged = false,
                        MergeCommit = complete.MergeCommitSha,
                        ErrorCode = "",
                    });
                    return ExitCodes.Success;

                case "stale_head":
                    EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                        "stale_head",
                        $"ADO refused to complete PR #{prNumber}: source branch advanced past the polled head SHA '{lastMergeSha}'. Re-poll and retry. Detail: {complete.ErrorBody}",
                        prUrl: prUrl);
                    return ExitCodes.Success;

                case "not_found":
                    EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                        "pr_not_found",
                        $"PR #{prNumber} disappeared between poll and complete in {slug}.",
                        prUrl: prUrl);
                    return ExitCodes.Success;

                case "completion_pending":
                    EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                        "completion_pending",
                        $"ADO accepted the complete-PR PATCH for PR #{prNumber} but the PR did not transition to status=completed within the poll budget. The merge may still land asynchronously, or the PR may be blocked by a policy. Inspect via `az repos pr show --id {prNumber}` and consider `az repos pr update --id {prNumber} --status completed` to land it manually. Detail: {complete.ErrorBody}",
                        prUrl: prUrl);
                    return ExitCodes.Success;

                case "not_mergeable":
                default:
                    EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                        "not_mergeable",
                        $"ADO refused to complete PR #{prNumber} (HTTP {complete.HttpStatus}, status={complete.Status}): {complete.ErrorBody}",
                        prUrl: prUrl);
                    return ExitCodes.Success;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat" : "ado_failed";
            EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                code, ex.Message);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitMergeEvidenceAdoError(organization, project, repository, slug, prNumber,
                "ado_failed", ex.Message);
            return ExitCodes.Success;
        }
    }

    private static void EmitMergeEvidenceAdo(PrMergeEvidenceAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrMergeEvidenceAdoResult));

    private static void EmitMergeEvidenceAdoError(
        string organization,
        string project,
        string repository,
        string slug,
        int prNumber,
        string errorCode,
        string message,
        string prUrl = "",
        bool merged = false)
    {
        EmitMergeEvidenceAdo(new PrMergeEvidenceAdoResult
        {
            Organization = organization,
            Project = project,
            Repository = repository,
            RepoSlug = slug,
            PrNumber = prNumber,
            PrUrl = prUrl,
            Merged = merged,
            AlreadyMerged = false,
            MergeCommit = "",
            ErrorCode = errorCode,
            Error = message,
        });
    }
}
