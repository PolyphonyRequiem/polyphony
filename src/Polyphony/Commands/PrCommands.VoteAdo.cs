using System.Net;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Routing;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Submit a reviewer vote on an Azure DevOps pull request — the ADO
    /// equivalent of <c>gh pr review --approve|--request-changes|--comment</c>.
    /// Maps the human-friendly vote name to ADO's reviewer enum and PATCHes
    /// the reviewers endpoint via <see cref="IAdoClient.SetPullRequestVoteAsync"/>.
    ///
    /// <para>Always exits 0 — routing-style verb. Errors surface in the
    /// <c>error</c> + <c>error_code</c> fields of the JSON envelope rather
    /// than via process exit codes.</para>
    ///
    /// <para>The reviewer GUID must be supplied explicitly. Resolving it
    /// from the PAT identity (or from <c>az devops invoke</c>) is a future
    /// enhancement; v1 takes the explicit-arg path so the verb has no
    /// hidden network dependencies beyond the single PATCH.</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">Repository identifier — GUID or name; both accepted by ADO.</param>
    /// <param name="prNumber">Pull request ID (positive integer).</param>
    /// <param name="reviewerId">Reviewer's identity GUID.</param>
    /// <param name="vote">Vote name: <c>approve</c>, <c>approve-with-suggestions</c>, <c>reject</c>, <c>wait-for-author</c>, or <c>reset</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("vote-ado")]
    [VerbResult(typeof(PrVoteAdoResult))]
    public async Task<int> VoteAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int prNumber = RequiredInput.MissingInt,
        string reviewerId = "",
        string vote = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr vote-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--pr-number", prNumber == RequiredInput.MissingInt),
            ("--reviewer-id", string.IsNullOrEmpty(reviewerId)),
            ("--vote", string.IsNullOrEmpty(vote))) is { } halt)
            return halt;

        var prUrl = BuildAdoPrUrl(organization, project, repository, prNumber);
        var slug = BuildAdoSlug(organization, project, repository);

        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository))
        {
            EmitVoteAdoError(
                prUrl, slug, prNumber, reviewerId, vote, voteValue: 0,
                "organization, project, and repository are required",
                "invalid_argument");
            return ExitCodes.Success;
        }
        if (prNumber <= 0)
        {
            EmitVoteAdoError(
                prUrl, slug, prNumber, reviewerId, vote, voteValue: 0,
                $"prNumber must be a positive integer (got {prNumber})",
                "invalid_argument");
            return ExitCodes.Success;
        }
        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            EmitVoteAdoError(
                prUrl, slug, prNumber, reviewerId ?? string.Empty, vote, voteValue: 0,
                "reviewerId is required",
                "invalid_argument");
            return ExitCodes.Success;
        }
        if (!TryMapVoteName(vote, out var voteValue))
        {
            EmitVoteAdoError(
                prUrl, slug, prNumber, reviewerId, vote ?? string.Empty, voteValue: 0,
                $"vote '{vote}' is not one of: approve, approve-with-suggestions, reject, wait-for-author, reset",
                "invalid_vote");
            return ExitCodes.Success;
        }
        if (ado is null)
        {
            // Shouldn't happen in production (DI registers IAdoClient) but the
            // ctor allows null so unit tests can opt out of the ADO leg.
            EmitVoteAdoError(
                prUrl, slug, prNumber, reviewerId, vote, voteValue,
                "IAdoClient is not configured",
                "ado_failed");
            return ExitCodes.Success;
        }

        try
        {
            var ok = await ado.SetPullRequestVoteAsync(
                organization, project, repository, prNumber, reviewerId, voteValue, ct)
                .ConfigureAwait(false);
            if (!ok)
            {
                EmitVoteAdoError(
                    prUrl, slug, prNumber, reviewerId, vote, voteValue,
                    $"PR #{prNumber} or reviewer {reviewerId} not found in {slug}",
                    "pr_not_found");
                return ExitCodes.Success;
            }

            EmitVoteAdo(new PrVoteAdoResult
            {
                PrNumber = prNumber,
                ReviewerId = reviewerId,
                Vote = vote,
                VoteValue = voteValue,
                Submitted = true,
                RepoSlug = slug,
                PrUrl = prUrl,
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            // Raised by AdoClient.ResolvePatOrThrow when no PAT is configured.
            EmitVoteAdoError(prUrl, slug, prNumber, reviewerId, vote, voteValue, ex.Message, "no_pat");
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitVoteAdoError(prUrl, slug, prNumber, reviewerId, vote, voteValue, ex.Message, "ado_timeout");
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            // 401/403 → no_pat (PAT is missing or rejected); everything else → ado_failed.
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitVoteAdoError(prUrl, slug, prNumber, reviewerId, vote, voteValue, ex.Message, code);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitVoteAdoError(prUrl, slug, prNumber, reviewerId, vote, voteValue, ex.Message, "ado_failed");
            return ExitCodes.Success;
        }
    }

    /// <summary>
    /// Map the CLI vote name (kebab-case) to ADO's reviewer enum integer.
    /// Names match the GitHub <c>gh pr review</c> vocabulary where possible
    /// (<c>approve</c>) and use ADO's own terminology for the others.
    /// </summary>
    internal static bool TryMapVoteName(string? name, out int value)
    {
        switch (name)
        {
            case "approve":
                value = 10; return true;
            case "approve-with-suggestions":
                value = 5; return true;
            case "reset":
                value = 0; return true;
            case "wait-for-author":
                value = -5; return true;
            case "reject":
                value = -10; return true;
            default:
                value = 0; return false;
        }
    }

    private static void EmitVoteAdo(PrVoteAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrVoteAdoResult));

    private static void EmitVoteAdoError(
        string prUrl,
        string slug,
        int prNumber,
        string reviewerId,
        string vote,
        int voteValue,
        string message,
        string errorCode)
    {
        EmitVoteAdo(new PrVoteAdoResult
        {
            PrNumber = prNumber,
            ReviewerId = reviewerId,
            Vote = vote,
            VoteValue = voteValue,
            Submitted = false,
            RepoSlug = slug,
            PrUrl = prUrl,
            Error = message,
            ErrorCode = errorCode,
        });
    }
}
