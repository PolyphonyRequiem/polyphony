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
    /// Harvest the human-authored review comments on an Azure DevOps pull
    /// request — the read-side counterpart to
    /// <see cref="PostCommentAdo"/>, and the closing piece of ADO PR
    /// remediation parity tracked in
    /// <c>docs/decisions/ado-feature-pr-parity.md</c>. Closes the gap that
    /// previously forced the <c>feature-pr.yaml</c> remediation planner to
    /// reason from reviewer identity + vote alone on the ADO branch.
    ///
    /// <para>Always exits 0 — routing-style verb. Errors surface in the
    /// <c>error</c> + <c>error_code</c> fields of the JSON envelope rather
    /// than via process exit codes.</para>
    ///
    /// <para>Comments are flattened from ADO's thread → comment hierarchy
    /// into one row per author-authored comment; thread-level fields
    /// (<c>thread_id</c>, <c>thread_status</c>, <c>file_path</c>,
    /// <c>line</c>) are denormalised onto each row. System comments and
    /// tombstoned content are filtered inside <see cref="IAdoClient"/>
    /// before the verb sees them.</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">Repository identifier — GUID or name; both accepted by ADO.</param>
    /// <param name="prNumber">Pull request ID (positive integer).</param>
    /// <param name="includeResolved">
    /// When true, include comments from threads in <c>fixed</c>,
    /// <c>wontFix</c>, <c>closed</c>, or <c>byDesign</c> status. Defaults
    /// to false — the remediation planner only cares about open feedback.
    /// </param>
    /// <param name="since">
    /// Optional ISO 8601 UTC timestamp; comments published before this
    /// instant are filtered out. Empty / unset disables the filter. Invalid
    /// strings emit <c>invalid_argument</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("get-comments-ado")]
    [VerbResult(typeof(PrGetCommentsAdoResult))]
    public async Task<int> GetCommentsAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int prNumber = RequiredInput.MissingInt,
        bool includeResolved = false,
        string since = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr get-comments-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--pr-number", prNumber == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var prUrl = BuildAdoPrUrl(organization, project, repository, prNumber);
        var slug = BuildAdoSlug(organization, project, repository);

        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository))
        {
            EmitGetCommentsAdoError(
                prUrl, slug, prNumber,
                "organization, project, and repository are required",
                "invalid_argument");
            return ExitCodes.Success;
        }
        if (prNumber <= 0)
        {
            EmitGetCommentsAdoError(
                prUrl, slug, prNumber,
                $"prNumber must be a positive integer (got {prNumber})",
                "invalid_argument");
            return ExitCodes.Success;
        }

        DateTime? sinceUtc = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            // Parse strictly as ISO 8601; accept any RFC 3339 variant. Coerce
            // to UTC so the >= comparison against ADO's UTC publishedDate is
            // unambiguous regardless of caller timezone.
            if (!DateTime.TryParse(
                    since, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                EmitGetCommentsAdoError(
                    prUrl, slug, prNumber,
                    $"since '{since}' is not a valid ISO 8601 timestamp",
                    "invalid_argument");
                return ExitCodes.Success;
            }
            sinceUtc = parsed;
        }

        if (ado is null)
        {
            // Shouldn't happen in production (DI registers IAdoClient) but the
            // ctor allows null so unit tests can opt out of the ADO leg.
            EmitGetCommentsAdoError(
                prUrl, slug, prNumber,
                "IAdoClient is not configured",
                "ado_failed");
            return ExitCodes.Success;
        }

        try
        {
            var threads = await ado.ListPullRequestThreadsAsync(
                organization, project, repository, prNumber, ct).ConfigureAwait(false);
            if (threads is null)
            {
                EmitGetCommentsAdoError(
                    prUrl, slug, prNumber,
                    $"PR #{prNumber} not found in {slug}",
                    "pr_not_found");
                return ExitCodes.Success;
            }

            var flattened = FlattenComments(threads, includeResolved, sinceUtc);

            EmitGetCommentsAdo(new PrGetCommentsAdoResult
            {
                PrNumber = prNumber,
                RepoSlug = slug,
                PrUrl = prUrl,
                Count = flattened.Count,
                Comments = flattened,
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoAuthenticationException ex)
        {
            // Raised by IPolyphonyAuthProvider when no ADO credential chain succeeds (PAT env or AAD).
            EmitGetCommentsAdoError(prUrl, slug, prNumber, ex.Message, "no_pat");
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitGetCommentsAdoError(prUrl, slug, prNumber, ex.Message, "ado_timeout");
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            // 401/403 → no_pat (PAT is missing or rejected); everything else → ado_failed.
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitGetCommentsAdoError(prUrl, slug, prNumber, ex.Message, code);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitGetCommentsAdoError(prUrl, slug, prNumber, ex.Message, "ado_failed");
            return ExitCodes.Success;
        }
    }

    /// <summary>
    /// Flatten the per-thread projection into per-comment rows, applying the
    /// resolved + since filters. Thread context (id, status, file path, line)
    /// is denormalised onto every comment so consumers don't have to reason
    /// about the parent thread separately.
    /// </summary>
    private static IReadOnlyList<AdoPrComment> FlattenComments(
        IReadOnlyList<AdoPullRequestThread> threads,
        bool includeResolved,
        DateTime? sinceUtc)
    {
        var rows = new List<AdoPrComment>();
        foreach (var thread in threads)
        {
            if (!includeResolved && thread.IsResolved) continue;

            foreach (var comment in thread.Comments)
            {
                if (sinceUtc is { } cutoff && comment.PublishedAt is { } published
                    && published.ToUniversalTime() < cutoff)
                {
                    continue;
                }

                rows.Add(new AdoPrComment
                {
                    Id = comment.Id,
                    ThreadId = thread.Id,
                    ParentCommentId = comment.ParentCommentId,
                    Author = comment.Author,
                    Body = comment.Body,
                    FilePath = thread.FilePath,
                    Line = thread.Line,
                    PublishedAt = comment.PublishedAt,
                    LastUpdatedAt = comment.LastUpdatedAt,
                    IsResolved = thread.IsResolved,
                    // ADO does not expose a direct outdated-comment signal in
                    // the threads payload; surfacing it would require
                    // correlating thread iteration context against the PR's
                    // latest iteration. Pinned to false for parity with the
                    // GitHub-side field shape.
                    IsOutdated = false,
                    ThreadStatus = thread.Status,
                    CommentType = comment.CommentType,
                });
            }
        }
        return rows;
    }

    private static void EmitGetCommentsAdo(PrGetCommentsAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrGetCommentsAdoResult));

    private static void EmitGetCommentsAdoError(
        string prUrl,
        string slug,
        int prNumber,
        string message,
        string errorCode)
    {
        EmitGetCommentsAdo(new PrGetCommentsAdoResult
        {
            PrNumber = prNumber,
            RepoSlug = slug,
            PrUrl = prUrl,
            Count = 0,
            Comments = Array.Empty<AdoPrComment>(),
            Error = message,
            ErrorCode = errorCode,
        });
    }
}
