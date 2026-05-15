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
    /// Open (or reuse) the pull request that promotes a feature branch into
    /// the configured target branch on Azure DevOps. ADO analogue of
    /// <c>polyphony pr create-feature-pr</c>.
    ///
    /// <para>Head is <c>feature/{root_id}</c>; base defaults to <c>main</c>
    /// but can be overridden via <paramref name="targetBranch"/>. Reuses an
    /// existing OPEN PR for the same head/base pair instead of creating a
    /// duplicate (idempotent).</para>
    ///
    /// <para><b>Routing-style exit code</b> — always exits 0; consumers
    /// branch on <see cref="PrCreateFeatureAdoResult.ErrorCode"/>. Mirrors
    /// <c>open-mg-ado</c> (#106) and <c>open-plan-ado</c> (#104).</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name; both accepted.</param>
    /// <param name="rootId">Root work-item id of the run's apex (focus) item.</param>
    /// <param name="targetBranch">Target branch (typically <c>main</c>); defaults to <c>main</c>.</param>
    /// <param name="title">Optional PR title; deterministic fallback (derived from the work-item title via twig) used when empty.</param>
    /// <param name="body">Optional PR body; minimal deterministic fallback used when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("create-feature-ado")]
    [VerbResult(typeof(PrCreateFeatureAdoResult))]
    public async Task<int> CreateFeatureAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int rootId = RequiredInput.MissingInt,
        string targetBranch = "main",
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr create-feature-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--root-id", rootId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var slug = BuildAdoSlug(organization, project, repository);

        // ── 1. Validate inputs. ────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository))
        {
            EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                "invalid_argument", "organization, project, and repository are required");
            return ExitCodes.Success;
        }
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                "invalid_argument", "targetBranch is required");
            return ExitCodes.Success;
        }
        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                "invalid_argument", $"rootId must be positive (got {rootId})");
            return ExitCodes.Success;
        }

        var headBranch = BranchNameBuilder.Feature(root).Value;
        var baseBranch = targetBranch;

        if (ado is null)
        {
            EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                "ado_failed", "IAdoClient is not configured", headBranch, baseBranch);
            return ExitCodes.Success;
        }

        // ── 2. Validate head + base exist on the remote — gives a clean
        //      categorical error instead of letting ADO fail late with a less
        //      actionable message. Mirrors the GitHub-side create-feature-pr
        //      verb, which checks ls-remote for the head branch.
        try
        {
            var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", ct).ConfigureAwait(false);
            if (headRefs.Count == 0)
            {
                EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                    "missing_head_branch", $"head branch '{headBranch}' does not exist on remote",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{baseBranch}", ct).ConfigureAwait(false);
            if (baseRefs.Count == 0)
            {
                EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                    "missing_base_branch", $"base branch '{baseBranch}' does not exist on remote",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                "ado_failed", $"git ls-remote failed: {ex.Message}", headBranch, baseBranch);
            return ExitCodes.Success;
        }

        var prTitle = string.IsNullOrWhiteSpace(title)
            ? await ResolvePrTitleAsync(rootId, ct).ConfigureAwait(false)
            : title;
        var prBody = string.IsNullOrWhiteSpace(body)
            ? await BuildPrBodyAsync(rootId, headBranch, baseBranch, ct).ConfigureAwait(false)
            : body;

        try
        {
            // ── 3. Reuse check: scan active PRs for a matching source/target. ─
            var activePrs = await ado.ListPullRequestsAsync(
                organization, project, repository,
                AdoPullRequestStatus.Active, null, ct).ConfigureAwait(false);

            if (activePrs is null)
            {
                EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            var expectedSourceRef = "refs/heads/" + headBranch;
            var expectedTargetRef = "refs/heads/" + baseBranch;
            AdoPullRequest? existing = null;
            foreach (var pr in activePrs)
            {
                if (string.Equals(pr.SourceRefName, expectedSourceRef, StringComparison.Ordinal)
                    && string.Equals(pr.TargetRefName, expectedTargetRef, StringComparison.Ordinal))
                {
                    existing = pr;
                    break;
                }
            }

            if (existing is not null)
            {
                EmitCreateFeatureAdo(new PrCreateFeatureAdoResult
                {
                    RootId = rootId,
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

            // ── 4. Create the PR. ──────────────────────────────────────────
            var created = await ado.CreatePullRequestAsync(
                organization, project, repository,
                sourceBranch: headBranch,
                targetBranch: baseBranch,
                title: prTitle,
                description: prBody,
                ct).ConfigureAwait(false);

            if (created is null)
            {
                EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            EmitCreateFeatureAdo(new PrCreateFeatureAdoResult
            {
                RootId = rootId,
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
            // Raised by AdoClient.ResolvePatOrThrow when no PAT is configured.
            EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                "no_pat", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                "ado_timeout", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            // 401/403 → no_pat (PAT is missing or rejected); everything else → ado_failed.
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                code, ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                "ado_failed", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
    }

    private static void EmitCreateFeatureAdo(PrCreateFeatureAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrCreateFeatureAdoResult));

    private static void EmitCreateFeatureAdoError(
        int rootId,
        string targetBranch,
        string organization,
        string project,
        string repository,
        string slug,
        string errorCode,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitCreateFeatureAdo(new PrCreateFeatureAdoResult
        {
            RootId = rootId,
            HeadBranch = headBranch,
            BaseBranch = string.IsNullOrEmpty(baseBranch) ? (targetBranch ?? string.Empty) : baseBranch,
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
