using System.Net;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
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
    [JournaledAction(Action = "pr_post_comment_ado")]
    [VerbResult(typeof(PrPostCommentAdoResult))]
    public async Task<int> PostCommentAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int prNumber = RequiredInput.MissingInt,
        string body = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr post-comment-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--pr-number", prNumber == RequiredInput.MissingInt),
            ("--body", string.IsNullOrEmpty(body))) is { } halt)
            return halt;

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

        PrPostCommentAdoPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("pr_post_comment_ado", PullRequestJournalTarget(prUrl, prNumber), null, null),
            async innerCt =>
            {
                if (ado is null)
                {
                    const string error = "IAdoClient is not configured";
                    payload = new PrPostCommentAdoPayload
                    {
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        RepoSlug = slug,
                        PrNumber = prNumber,
                        PrUrl = prUrl,
                        Body = bodyEcho,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = "ado_failed",
                        Error = error,
                    };
                    EmitPostCommentAdoError(prUrl, slug, prNumber, bodyEcho, error, "ado_failed");
                    return ExitCodes.Success;
                }

                try
                {
                    var posted = await ado.CreatePullRequestCommentThreadAsync(
                        organization, project, repository, prNumber, body, innerCt)
                        .ConfigureAwait(false);
                    if (posted is null)
                    {
                        var error = $"PR #{prNumber} not found in {slug}";
                        payload = new PrPostCommentAdoPayload
                        {
                            Organization = organization,
                            Project = project,
                            Repository = repository,
                            RepoSlug = slug,
                            PrNumber = prNumber,
                            PrUrl = prUrl,
                            Body = bodyEcho,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            ErrorCode = "pr_not_found",
                            Error = error,
                        };
                        EmitPostCommentAdoError(prUrl, slug, prNumber, bodyEcho, error, "pr_not_found");
                        return ExitCodes.Success;
                    }

                    var result = new PrPostCommentAdoResult
                    {
                        PrNumber = prNumber,
                        Body = bodyEcho,
                        Posted = true,
                        ThreadId = posted.ThreadId,
                        CommentId = posted.CommentId,
                        RepoSlug = slug,
                        PrUrl = prUrl,
                    };
                    payload = new PrPostCommentAdoPayload
                    {
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        RepoSlug = slug,
                        PrNumber = result.PrNumber,
                        PrUrl = result.PrUrl,
                        Body = result.Body,
                        ThreadId = result.ThreadId,
                        CommentId = result.CommentId,
                        ResultAction = "posted",
                        Succeeded = true,
                        WasMutated = true,
                    };
                    EmitPostCommentAdo(result);
                    return ExitCodes.Success;
                }
                catch (OperationCanceledException) { throw; }
                catch (AdoAuthenticationException ex)
                {
                    payload = new PrPostCommentAdoPayload
                    {
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        RepoSlug = slug,
                        PrNumber = prNumber,
                        PrUrl = prUrl,
                        Body = bodyEcho,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = "no_pat",
                        Error = ex.Message,
                    };
                    EmitPostCommentAdoError(prUrl, slug, prNumber, bodyEcho, ex.Message, "no_pat");
                    return ExitCodes.Success;
                }
                catch (TimeoutException ex)
                {
                    payload = new PrPostCommentAdoPayload
                    {
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        RepoSlug = slug,
                        PrNumber = prNumber,
                        PrUrl = prUrl,
                        Body = bodyEcho,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = "ado_timeout",
                        Error = ex.Message,
                    };
                    EmitPostCommentAdoError(prUrl, slug, prNumber, bodyEcho, ex.Message, "ado_timeout");
                    return ExitCodes.Success;
                }
                catch (HttpRequestException ex)
                {
                    var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "no_pat"
                        : "ado_failed";
                    payload = new PrPostCommentAdoPayload
                    {
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        RepoSlug = slug,
                        PrNumber = prNumber,
                        PrUrl = prUrl,
                        Body = bodyEcho,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = code,
                        Error = ex.Message,
                    };
                    EmitPostCommentAdoError(prUrl, slug, prNumber, bodyEcho, ex.Message, code);
                    return ExitCodes.Success;
                }
                catch (Exception ex)
                {
                    payload = new PrPostCommentAdoPayload
                    {
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        RepoSlug = slug,
                        PrNumber = prNumber,
                        PrUrl = prUrl,
                        Body = bodyEcho,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = "ado_failed",
                        Error = ex.Message,
                    };
                    EmitPostCommentAdoError(prUrl, slug, prNumber, bodyEcho, ex.Message, "ado_failed");
                    return ExitCodes.Success;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.PrPostCommentAdoPayload),
            ct: ct).ConfigureAwait(false);
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
