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
    /// Post a single advisory comment to an Azure DevOps pull request — the
    /// ADO equivalent of <c>gh pr review {prNumber} --comment --body "..."</c>.
    /// Creates a closed thread (status: 4) carrying one top-level text
    /// comment, via <see cref="IAdoClient.CreatePullRequestCommentThreadAsync"/>.
    ///
    /// <para>Always exits 0 — routing-style verb. Errors surface in the
    /// <c>error</c> + <c>error_code</c> fields of the JSON envelope rather
    /// than via process exit codes.</para>
    ///
    /// <para>The thread is created as <i>closed</i> because the comment is
    /// advisory: there is no expected reply or follow-up state machine for
    /// the verb to model. Callers needing an open discussion thread should
    /// add a separate verb that exposes the thread status enum.</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">Repository identifier — GUID or name; both accepted by ADO.</param>
    /// <param name="prNumber">Pull request ID (positive integer).</param>
    /// <param name="body">The comment body (Markdown).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("post-comment-ado")]
    [VerbResult(typeof(PrPostCommentAdoResult))]
    public async Task<int> PostCommentAdo(
        string organization,
        string project,
        string repository,
        int prNumber,
        string body,
        CancellationToken ct = default)
    {
        var prUrl = BuildAdoPrUrl(organization, project, repository, prNumber);
        var slug = BuildAdoSlug(organization, project, repository);
        var bodyEcho = body ?? string.Empty;

        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository))
        {
            EmitPostCommentAdoError(
                prUrl, slug, prNumber, bodyEcho,
                "organization, project, and repository are required",
                "invalid_argument");
            return ExitCodes.Success;
        }
        if (prNumber <= 0)
        {
            EmitPostCommentAdoError(
                prUrl, slug, prNumber, bodyEcho,
                $"prNumber must be a positive integer (got {prNumber})",
                "invalid_argument");
            return ExitCodes.Success;
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            EmitPostCommentAdoError(
                prUrl, slug, prNumber, bodyEcho,
                "body is required",
                "invalid_argument");
            return ExitCodes.Success;
        }
        if (ado is null)
        {
            // Shouldn't happen in production (DI registers IAdoClient) but the
            // ctor allows null so unit tests can opt out of the ADO leg.
            EmitPostCommentAdoError(
                prUrl, slug, prNumber, bodyEcho,
                "IAdoClient is not configured",
                "ado_failed");
            return ExitCodes.Success;
        }

        try
        {
            var posted = await ado.CreatePullRequestCommentThreadAsync(
                organization, project, repository, prNumber, body, ct)
                .ConfigureAwait(false);
            if (posted is null)
            {
                EmitPostCommentAdoError(
                    prUrl, slug, prNumber, bodyEcho,
                    $"PR #{prNumber} not found in {slug}",
                    "pr_not_found");
                return ExitCodes.Success;
            }

            EmitPostCommentAdo(new PrPostCommentAdoResult
            {
                PrNumber = prNumber,
                Body = bodyEcho,
                Posted = true,
                ThreadId = posted.ThreadId,
                CommentId = posted.CommentId,
                RepoSlug = slug,
                PrUrl = prUrl,
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            // Raised by AdoClient.ResolvePatOrThrow when no PAT is configured.
            EmitPostCommentAdoError(prUrl, slug, prNumber, bodyEcho, ex.Message, "no_pat");
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitPostCommentAdoError(prUrl, slug, prNumber, bodyEcho, ex.Message, "ado_timeout");
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            // 401/403 → no_pat (PAT is missing or rejected); everything else → ado_failed.
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitPostCommentAdoError(prUrl, slug, prNumber, bodyEcho, ex.Message, code);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitPostCommentAdoError(prUrl, slug, prNumber, bodyEcho, ex.Message, "ado_failed");
            return ExitCodes.Success;
        }
    }

    private static void EmitPostCommentAdo(PrPostCommentAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrPostCommentAdoResult));

    private static void EmitPostCommentAdoError(
        string prUrl,
        string slug,
        int prNumber,
        string body,
        string message,
        string errorCode)
    {
        EmitPostCommentAdo(new PrPostCommentAdoResult
        {
            PrNumber = prNumber,
            Body = body,
            Posted = false,
            ThreadId = null,
            CommentId = null,
            RepoSlug = slug,
            PrUrl = prUrl,
            Error = message,
            ErrorCode = errorCode,
        });
    }
}
