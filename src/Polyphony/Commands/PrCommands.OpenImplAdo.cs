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

        if (ado is null)
        {
            EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "ado_failed", "IAdoClient is not configured", headBranch, baseBranch);
            return ExitCodes.Success;
        }

        // ── 2. Validate head + base exist on the remote. ───────────────────
        try
        {
            var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", ct).ConfigureAwait(false);
            if (headRefs.Count == 0)
            {
                EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "missing_head_branch", $"head branch '{headBranch}' does not exist on remote",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{baseBranch}", ct).ConfigureAwait(false);
            if (baseRefs.Count == 0)
            {
                EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "missing_base_branch", $"base branch '{baseBranch}' does not exist on remote",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "ado_failed", $"git ls-remote failed: {ex.Message}", headBranch, baseBranch);
            return ExitCodes.Success;
        }

        var prTitle = string.IsNullOrWhiteSpace(title)
            ? await ResolveImplPrTitleAsync(itemId, ct).ConfigureAwait(false)
            : title;
        var prBody = string.IsNullOrWhiteSpace(body)
            ? BuildDefaultImplBody(rootId, itemId, path.Canonical, headBranch, baseBranch)
            : body;

        try
        {
            // ── 3. Reuse check: ADO list filtered by source ref. ──────────
            var activePrs = await ado.ListPullRequestsAsync(
                organization, project, repository,
                AdoPullRequestStatus.Active, sourceBranch: headBranch, ct).ConfigureAwait(false);

            if (activePrs is null)
            {
                EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
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
                EmitOpenImplAdo(new PrOpenImplAdoResult
                {
                    RootId = rootId,
                    ItemId = itemId,
                    MgPath = path.Canonical,
                    HeadBranch = headBranch,
                    BaseBranch = baseBranch,
                    Organization = organization,
                    Project = project,
                    Repository = repository,
                    RepoSlug = slug,
                    PrNumber = existing.PullRequestId,
                    PrUrl = !string.IsNullOrEmpty(existing.Url)
                        ? existing.Url
                        : BuildAdoPrUrl(organization, project, repository, existing.PullRequestId),
                    Title = prTitle,
                    Created = false,
                    ErrorCode = "",
                });
                return ExitCodes.Success;
            }

            // ── 4. Create the PR. ─────────────────────────────────────────
            var created = await ado.CreatePullRequestAsync(
                organization, project, repository,
                sourceBranch: headBranch,
                targetBranch: baseBranch,
                title: prTitle,
                description: prBody,
                ct).ConfigureAwait(false);

            if (created is null)
            {
                EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            EmitOpenImplAdo(new PrOpenImplAdoResult
            {
                RootId = rootId,
                ItemId = itemId,
                MgPath = path.Canonical,
                HeadBranch = headBranch,
                BaseBranch = baseBranch,
                Organization = organization,
                Project = project,
                Repository = repository,
                RepoSlug = slug,
                PrNumber = created.PullRequestId,
                PrUrl = !string.IsNullOrEmpty(created.Url)
                    ? created.Url
                    : BuildAdoPrUrl(organization, project, repository, created.PullRequestId),
                Title = prTitle,
                Created = true,
                ErrorCode = "",
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "no_pat", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "ado_timeout", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                code, ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitOpenImplAdoError(rootId, itemId, mgPath, organization, project, repository, slug,
                "ado_failed", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
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
