using System.Net;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.AzureDevOps;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes an impl branch into
    /// its enclosing merge-group branch on Azure DevOps. ADO analogue of
    /// <c>polyphony pr open-impl-pr</c>.
    ///
    /// <para>Head is <c>impl/{root_id}-{item_id}</c>; base is
    /// <c>mg/{root_id}_{mg_path}</c>. Reuses an existing OPEN PR for the
    /// same head/base pair instead of creating a duplicate (idempotent).</para>
    ///
    /// <para><b>Routing-style exit code</b> — always exits 0; consumers
    /// branch on <see cref="PrOpenImplAdoResult.ErrorCode"/>. Mirrors
    /// <c>open-mg-ado</c>.</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name; both accepted.</param>
    /// <param name="rootId">Root work-item id of the run's apex (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the task.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path of the enclosing MG.</param>
    /// <param name="title">Optional PR title; deterministic fallback derived from the cached work-item title.</param>
    /// <param name="body">Optional PR body; minimal deterministic fallback used when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-impl-ado")]
    [VerbResult(typeof(PrOpenImplAdoResult))]
    public async Task<int> OpenImplAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        string mgPath = "",
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr open-impl-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        var slug = BuildAdoSlug(organization, project, repository);

        // ── 1. Validate inputs. ────────────────────────────────────────────
        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "invalid_argument", $"rootId must be positive (got {rootId})");
            return ExitCodes.Success;
        }
        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "invalid_argument", $"itemId must be positive (got {itemId})");
            return ExitCodes.Success;
        }
        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "invalid_argument",
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.Success;
        }

        var headBranch = BranchNameBuilder.Impl(root, item).Value;
        var baseBranch = BranchNameBuilder.MergeGroup(root, path).Value;

        var outcome = await OpenImplAdoCoreAsync(
            organization, project, repository, slug,
            rootId, itemId, path,
            headBranch, baseBranch,
            title, body, ct).ConfigureAwait(false);

        EmitOpenImplAdo(new PrOpenImplAdoResult
        {
            RootId = rootId,
            ItemId = itemId,
            MgPath = path.Canonical,
            HeadBranch = outcome.HeadBranch,
            BaseBranch = outcome.BaseBranch,
            Organization = organization,
            Project = project,
            Repository = repository,
            RepoSlug = slug,
            PrNumber = outcome.PrNumber,
            PrUrl = outcome.PrUrl,
            Title = outcome.Title,
            Created = outcome.Created,
            ErrorCode = outcome.ErrorCode,
            Error = outcome.Error,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Shared ADO logic for opening an impl PR. Used by
    /// <see cref="OpenImplAdo"/> (legacy ADO-only envelope) and by
    /// <see cref="OpenImplPr"/>'s ADO branch (unified envelope).
    /// </summary>
    internal async Task<ImplAdoOutcome> OpenImplAdoCoreAsync(
        string organization,
        string project,
        string repository,
        string slug,
        int rootId,
        int itemId,
        MergeGroupPath path,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        CancellationToken ct)
    {
        if (ado is null)
        {
            return ImplAdoOutcome.Failure(headBranch, baseBranch,
                "ado_failed", "IAdoClient is not configured");
        }

        try
        {
            var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", ct).ConfigureAwait(false);
            if (headRefs.Count == 0)
            {
                return ImplAdoOutcome.Failure(headBranch, baseBranch,
                    "missing_head_branch", $"head branch '{headBranch}' does not exist on remote");
            }

            var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{baseBranch}", ct).ConfigureAwait(false);
            if (baseRefs.Count == 0)
            {
                return ImplAdoOutcome.Failure(headBranch, baseBranch,
                    "missing_base_branch", $"base branch '{baseBranch}' does not exist on remote");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ImplAdoOutcome.Failure(headBranch, baseBranch,
                "ado_failed", $"git ls-remote failed: {ex.Message}");
        }

        var prTitle = string.IsNullOrWhiteSpace(title)
            ? await ResolveImplPrTitleAsync(itemId, ct).ConfigureAwait(false)
            : title;
        var prBody = string.IsNullOrWhiteSpace(body)
            ? BuildDefaultImplBody(rootId, itemId, path.Canonical, headBranch, baseBranch)
            : body;

        try
        {
            var activePrs = await ado.ListPullRequestsAsync(
                organization, project, repository,
                AdoPullRequestStatus.Active, sourceBranch: headBranch, ct).ConfigureAwait(false);

            if (activePrs is null)
            {
                return ImplAdoOutcome.Failure(headBranch, baseBranch,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.");
            }

            var expectedTargetRef = "refs/heads/" + baseBranch;
            AdoPullRequest? existing = null;
            foreach (var pr in activePrs)
            {
                if (string.Equals(pr.TargetRefName, expectedTargetRef, StringComparison.Ordinal))
                {
                    existing = pr;
                    break;
                }
            }

            if (existing is not null)
            {
                return new ImplAdoOutcome(
                    PrNumber: existing.PullRequestId,
                    PrUrl: BuildAdoPrUrl(organization, project, repository, existing.PullRequestId),
                    Title: prTitle,
                    HeadBranch: headBranch,
                    BaseBranch: baseBranch,
                    Created: false,
                    ErrorCode: "",
                    Error: null);
            }

            var created = await ado.CreatePullRequestAsync(
                organization, project, repository,
                sourceBranch: headBranch,
                targetBranch: baseBranch,
                title: prTitle,
                description: prBody,
                ct).ConfigureAwait(false);

            if (created is null)
            {
                return ImplAdoOutcome.Failure(headBranch, baseBranch,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.");
            }

            return new ImplAdoOutcome(
                PrNumber: created.PullRequestId,
                PrUrl: BuildAdoPrUrl(organization, project, repository, created.PullRequestId),
                Title: prTitle,
                HeadBranch: headBranch,
                BaseBranch: baseBranch,
                Created: true,
                ErrorCode: "",
                Error: null);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            return ImplAdoOutcome.Failure(headBranch, baseBranch, "no_pat", ex.Message);
        }
        catch (TimeoutException ex)
        {
            return ImplAdoOutcome.Failure(headBranch, baseBranch, "ado_timeout", ex.Message);
        }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            return ImplAdoOutcome.Failure(headBranch, baseBranch, code, ex.Message);
        }
        catch (Exception ex)
        {
            return ImplAdoOutcome.Failure(headBranch, baseBranch, "ado_failed", ex.Message);
        }
    }

    /// <summary>
    /// Internal carrier for the platform-neutral fields produced by
    /// <see cref="OpenImplAdoCoreAsync"/>.
    /// </summary>
    internal readonly record struct ImplAdoOutcome(
        int PrNumber,
        string PrUrl,
        string Title,
        string HeadBranch,
        string BaseBranch,
        bool Created,
        string ErrorCode,
        string? Error)
    {
        public static ImplAdoOutcome Failure(
            string headBranch, string baseBranch, string errorCode, string message)
            => new(
                PrNumber: 0,
                PrUrl: string.Empty,
                Title: string.Empty,
                HeadBranch: headBranch,
                BaseBranch: baseBranch,
                Created: false,
                ErrorCode: errorCode,
                Error: message);
    }

    private static void EmitOpenImplAdo(PrOpenImplAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenImplAdoResult));

    private static void EmitOpenImplAdoError(
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
        string baseBranch = "")
    {
        EmitOpenImplAdo(new PrOpenImplAdoResult
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
            PrNumber = 0,
            PrUrl = string.Empty,
            Title = string.Empty,
            Created = false,
            ErrorCode = errorCode,
            Error = message,
        });
    }
}
