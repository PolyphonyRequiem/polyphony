using System.Globalization;
using System.Net;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
using Polyphony.Routing;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Poll an Azure DevOps pull request and emit the same platform-neutral
    /// <see cref="PrPollStatusResult"/> envelope as the GitHub-side
    /// <see cref="PollStatus"/> verb. Composes its result from
    /// <see cref="IAdoClient.GetPullRequestPollDataAsync"/> (one PR detail
    /// call + one reviewers call) and applies the same state / mergeability /
    /// policy normalisation rules so a workflow can route on
    /// <c>state</c> + <c>policy.merge_allowed</c> without caring about the
    /// underlying platform.
    ///
    /// <para>Always exits 0 — routing-style verb. Errors surface in the
    /// <c>error</c> + <c>error_code</c> fields of the JSON envelope rather
    /// than via process exit codes.</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repositoryId">Repository identifier — GUID or name; both accepted by ADO.</param>
    /// <param name="prNumber">Pull request ID (positive integer).</param>
    /// <param name="includeMetadata">When true, parse plan-PR YAML front-matter from the PR body.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("poll-status-ado")]
    [VerbResult(typeof(PrPollStatusResult))]
    public async Task<int> PollStatusAdo(
        string organization = "",
        string project = "",
        string repositoryId = "",
        int prNumber = RequiredInput.MissingInt,
        bool includeMetadata = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr poll-status-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository-id", string.IsNullOrEmpty(repositoryId)),
            ("--pr-number", prNumber == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var prUrl = BuildAdoPrUrl(organization, project, repositoryId, prNumber);

        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repositoryId))
        {
            EmitPollStatusAdoError(
                prUrl,
                "organization, project, and repositoryId are required",
                "invalid_argument",
                slug: BuildAdoSlug(organization, project, repositoryId),
                prNumber: prNumber);
            return ExitCodes.Success;
        }
        if (prNumber <= 0)
        {
            EmitPollStatusAdoError(
                prUrl,
                $"prNumber must be a positive integer (got {prNumber})",
                "invalid_argument",
                slug: BuildAdoSlug(organization, project, repositoryId),
                prNumber: prNumber);
            return ExitCodes.Success;
        }
        if (ado is null)
        {
            // Shouldn't happen in production (DI registers IAdoClient) but the
            // ctor allows null so unit tests can opt out of the ADO leg.
            EmitPollStatusAdoError(
                prUrl,
                "IAdoClient is not configured",
                "ado_failed",
                slug: BuildAdoSlug(organization, project, repositoryId),
                prNumber: prNumber);
            return ExitCodes.Success;
        }

        var slug = BuildAdoSlug(organization, project, repositoryId);

        try
        {
            var data = await ado.GetPullRequestPollDataAsync(
                organization, project, repositoryId, prNumber, ct).ConfigureAwait(false);
            if (data is null)
            {
                EmitPollStatusAdoError(
                    prUrl,
                    $"PR #{prNumber} not found in {slug}",
                    "pr_not_found",
                    slug: slug,
                    prNumber: prNumber);
                return ExitCodes.Success;
            }

            // Fetch ADO threads alongside the PR detail. Threads drive the
            // changes_requested gate after option B (issue #207). Failure
            // to fetch is non-fatal — surface a warning and fall back to
            // the native review decision.
            List<PrPollThread> threads = [];
            List<string>? warnings = null;
            try
            {
                var rawThreads = await ado.ListPullRequestThreadsAsync(
                    organization, project, repositoryId, prNumber, ct).ConfigureAwait(false);
                if (rawThreads is null)
                {
                    (warnings ??= []).Add(
                        $"Could not fetch review threads for PR #{prNumber} (ADO returned 404 for /threads). " +
                        "Status field reflects native review decision only — unresolved review threads were not considered.");
                }
                else
                {
                    threads = rawThreads.Select(MapAdoThread).ToList();
                }
            }
            catch (Exception threadEx) when (threadEx is not OperationCanceledException)
            {
                (warnings ??= []).Add(
                    $"Could not fetch review threads for PR #{prNumber}: {threadEx.Message}. " +
                    "Status field reflects native review decision only.");
            }

            var reviewers = data.Reviews.Select(MapAdoReviewer).ToList();

            // ADO PR authors CAN vote on their own PRs, so the magic-comment
            // approve fallback is GitHub-only — never apply it on the ADO leg.
            var state = PrPollStateDerivation.DeriveState(
                data.State, data.ReviewDecision, threads, hasMagicApproveFromAuthor: false);

            var mergeable = MapAdoMergeable(data.Mergeable);
            var policy = ComputePolicy(state, mergeable);
            var metadata = includeMetadata ? PlanPrFrontMatter.Parse(data.Body) : null;

            var result = new PrPollStatusResult
            {
                PrUrl = prUrl,
                PrNumber = data.Number,
                RepoSlug = slug,
                State = state,
                HeadSha = data.HeadRefOid,
                HeadRef = data.HeadRefName,
                BaseRef = data.BaseRefName,
                Mergeable = mergeable,
                MergeCommitSha = data.MergeCommit,
                MergedAt = data.MergedAt?.ToString("o", CultureInfo.InvariantCulture),
                Reviewers = reviewers,
                Threads = threads,
                Policy = policy,
                Warnings = warnings,
                Metadata = metadata,
            };
            EmitPollStatusAdo(result);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoAuthenticationException ex)
        {
            // Raised by IPolyphonyAuthProvider when no ADO credential chain succeeds (PAT env or AAD).
            EmitPollStatusAdoError(prUrl, ex.Message, "no_pat", slug, prNumber);
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitPollStatusAdoError(prUrl, ex.Message, "ado_timeout", slug, prNumber);
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            // 401/403 → no_pat (PAT is missing or rejected); everything else → ado_failed.
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitPollStatusAdoError(prUrl, ex.Message, code, slug, prNumber);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitPollStatusAdoError(prUrl, ex.Message, "ado_failed", slug, prNumber);
            return ExitCodes.Success;
        }
    }

    /// <summary>
    /// Map a single ADO <see cref="AdoPullRequestThread"/> to the
    /// platform-neutral <see cref="PrPollThread"/> shape. ADO's
    /// <c>IsResolved</c> bit is already computed (status in {fixed,
    /// wontFix, closed, byDesign}); the raw status string is preserved
    /// in the <c>Status</c> field for diagnostics. ADO does not surface
    /// an outdated bit so <c>IsOutdated</c> is null.
    /// </summary>
    private static PrPollThread MapAdoThread(AdoPullRequestThread t)
    {
        var firstComment = t.Comments.Count > 0 ? t.Comments[0] : null;
        return new PrPollThread
        {
            Id = t.Id.ToString(CultureInfo.InvariantCulture),
            IsResolved = t.IsResolved,
            IsOutdated = null,
            Status = t.Status,
            AuthorIdentity = string.IsNullOrEmpty(firstComment?.Author) ? null : firstComment.Author,
            CreatedAt = firstComment?.PublishedAt?.ToString("o", CultureInfo.InvariantCulture),
            CommentCount = t.Comments.Count,
        };
    }

    private static bool? MapAdoMergeable(string adoMergeable) => adoMergeable.ToUpperInvariant() switch
    {
        "MERGEABLE" => true,
        "CONFLICTING" => false,
        _ => null,
    };

    private static PrPollReviewer MapAdoReviewer(AdoPullRequestReview review) => new()
    {
        Identity = review.Identity,
        Vote = review.Vote,
        SubmittedAt = review.SubmittedAt?.ToString("o", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Synthesise the canonical ADO PR URL — used as the <c>pr_url</c>
    /// echo-back field so consumers always have a stable handle to the PR
    /// regardless of how the verb was invoked.
    /// </summary>
    private static string BuildAdoPrUrl(string organization, string project, string repositoryId, int prNumber)
    {
        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repositoryId))
        {
            return string.Empty;
        }
        return $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}" +
               $"/_git/{Uri.EscapeDataString(repositoryId)}/pullrequest/{prNumber}";
    }

    /// <summary>
    /// Build the <c>repo_slug</c> field for ADO PRs in the form
    /// <c>org/project/repo</c>. Returns empty when any component is missing —
    /// the verb still emits the envelope, but the slug field is blank for the
    /// invalid-argument error path.
    /// </summary>
    private static string BuildAdoSlug(string organization, string project, string repositoryId)
    {
        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repositoryId))
        {
            return string.Empty;
        }
        return $"{organization}/{project}/{repositoryId}";
    }

    private static void EmitPollStatusAdo(PrPollStatusResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrPollStatusResult));

    private static void EmitPollStatusAdoError(
        string prUrl,
        string message,
        string errorCode,
        string slug,
        int prNumber)
    {
        var result = new PrPollStatusResult
        {
            PrUrl = prUrl,
            PrNumber = prNumber,
            RepoSlug = slug,
            State = "error",
            HeadSha = string.Empty,
            HeadRef = string.Empty,
            BaseRef = string.Empty,
            Mergeable = null,
            Reviewers = [],
            Threads = [],
            Policy = new PrPollPolicy { MergeAllowed = false, BlockingReasons = [message] },
            Error = message,
            ErrorCode = errorCode,
        };
        EmitPollStatusAdo(result);
    }
}
