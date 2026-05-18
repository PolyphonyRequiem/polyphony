using System.Net;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes a merge-group branch
    /// into its parent on Azure DevOps. ADO analogue of
    /// <c>polyphony pr open-mg-pr</c>.
    ///
    /// <para>Head is <c>mg/{root_id}_{mg_path}</c>; base is the parent
    /// merge-group branch when nested, or the feature branch when top-level.
    /// Reuses an existing OPEN PR for the same head/base pair instead of
    /// creating a duplicate (idempotent).</para>
    ///
    /// <para><b>Routing-style exit code</b> — always exits 0; consumers
    /// branch on <see cref="PrOpenMergeGroupAdoResult.ErrorCode"/>. Mirrors
    /// <c>open-plan-ado</c> (#104).</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name; both accepted.</param>
    /// <param name="rootId">Root work-item id of the run's apex (focus) item.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path.</param>
    /// <param name="title">Optional PR title; deterministic fallback used when empty.</param>
    /// <param name="body">Optional PR body; minimal deterministic fallback used when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-mg-ado")]
    [VerbResult(typeof(PrOpenMergeGroupAdoResult))]
    public async Task<int> OpenMergeGroupAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int rootId = RequiredInput.MissingInt,
        string mgPath = "",
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr open-mg-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        var slug = BuildAdoSlug(organization, project, repository);

        // ── 1. Validate inputs. ────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository))
        {
            EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "invalid_argument", "organization, project, and repository are required");
            return ExitCodes.Success;
        }
        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "invalid_argument", $"rootId must be positive (got {rootId})");
            return ExitCodes.Success;
        }
        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "invalid_argument",
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.Success;
        }

        var headBranch = BranchNameBuilder.MergeGroup(root, path).Value;
        var baseBranch = path.IsTopLevel
            ? BranchNameBuilder.Feature(root).Value
            : BranchNameBuilder.MergeGroup(root, MergeGroupPath.Of(path.Segments.Take(path.Depth - 1))).Value;

        if (ado is null)
        {
            EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "ado_failed", "IAdoClient is not configured", headBranch, baseBranch);
            return ExitCodes.Success;
        }

        // ── 2. Validate head + base exist on the remote — gives a clean
        //      categorical error instead of letting ADO fail late with a less
        //      actionable message. Mirrors the GitHub-side open-mg-pr verb.
        try
        {
            var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", ct).ConfigureAwait(false);
            if (headRefs.Count == 0)
            {
                EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "missing_head_branch", $"head branch '{headBranch}' does not exist on remote",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{baseBranch}", ct).ConfigureAwait(false);
            if (baseRefs.Count == 0)
            {
                EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "missing_base_branch", $"base branch '{baseBranch}' does not exist on remote",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "ado_failed", $"git ls-remote failed: {ex.Message}", headBranch, baseBranch);
            return ExitCodes.Success;
        }

        var prTitle = string.IsNullOrWhiteSpace(title)
            ? $"merge group {path.Canonical} for root #{rootId}"
            : title;
        var prBody = string.IsNullOrWhiteSpace(body)
            ? BuildDefaultMgAdoBody(rootId, path.Canonical, headBranch, baseBranch)
            : body;

        try
        {
            // ── 3. Reuse check: scan PRs for a matching source/target.
            //      Includes Completed PRs so a retry after a successful merge
            //      reuses the real merged PR rather than opening a degenerate
            //      no-op duplicate (AB#3228). Active PRs win over Completed
            //      when both exist for the same source/target.
            var allPrs = await ado.ListPullRequestsAsync(
                organization, project, repository,
                AdoPullRequestStatus.All, null, ct).ConfigureAwait(false);

            if (allPrs is null)
            {
                EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            var expectedSourceRef = "refs/heads/" + headBranch;
            var expectedTargetRef = "refs/heads/" + baseBranch;
            AdoPullRequest? activeMatch = null;
            AdoPullRequest? completedMatch = null;
            foreach (var pr in allPrs)
            {
                if (!string.Equals(pr.SourceRefName, expectedSourceRef, StringComparison.Ordinal)
                    || !string.Equals(pr.TargetRefName, expectedTargetRef, StringComparison.Ordinal))
                {
                    continue;
                }
                if (string.Equals(pr.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    activeMatch = pr;
                    break;
                }
                if (string.Equals(pr.Status, "completed", StringComparison.OrdinalIgnoreCase)
                    && (completedMatch is null || pr.CreationDate < completedMatch.CreationDate))
                {
                    // Prefer the OLDEST completed match: when a previous run
                    // produced a real merge and a subsequent retry opened a
                    // no-op duplicate (AB#3228 symptom), the older PR is the
                    // one with the populated merge commit. The newer phantom
                    // would re-trip `missing_merge_commit` on the merge verb.
                    completedMatch = pr;
                }
            }

            // Branch-recycle staleness check (AB#3211 root cause): if the
            // branch names were reused by a later run, the completed PR's
            // recorded source SHA no longer matches origin/{head}. Drop the
            // stale match so we create a fresh active PR.
            if (activeMatch is null && completedMatch is not null)
            {
                var validity = await ValidateCompletedAdoPrAsync(
                    organization, project, repository,
                    completedMatch.PullRequestId, headBranch, baseBranch, ct).ConfigureAwait(false);
                if (!validity.IsValid) completedMatch = null;
            }

            var existing = activeMatch ?? completedMatch;

            if (existing is not null)
            {
                EmitOpenMgAdo(new PrOpenMergeGroupAdoResult
                {
                    RootId = rootId,
                    MgPath = path.Canonical,
                    HeadBranch = headBranch,
                    BaseBranch = baseBranch,
                    Organization = organization,
                    Project = project,
                    Repository = repository,
                    RepoSlug = slug,
                    PrNumber = existing.PullRequestId,
                    PrUrl = BuildAdoPrUrl(organization, project, repository, existing.PullRequestId),
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
                EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            EmitOpenMgAdo(new PrOpenMergeGroupAdoResult
            {
                RootId = rootId,
                MgPath = path.Canonical,
                HeadBranch = headBranch,
                BaseBranch = baseBranch,
                Organization = organization,
                Project = project,
                Repository = repository,
                RepoSlug = slug,
                PrNumber = created.PullRequestId,
                PrUrl = BuildAdoPrUrl(organization, project, repository, created.PullRequestId),
                Title = prTitle,
                Created = true,
                ErrorCode = "",
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (AdoAuthenticationException ex)
        {
            // Raised by IPolyphonyAuthProvider when no ADO credential chain succeeds (PAT env or AAD).
            EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "no_pat", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "ado_timeout", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            // 401/403 → no_pat (PAT is missing or rejected); everything else → ado_failed.
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                code, ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitOpenMgAdoError(rootId, mgPath, organization, project, repository, slug,
                "ado_failed", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
    }

    private static string BuildDefaultMgAdoBody(int rootId, string mgPath, string headBranch, string baseBranch)
    {
        var sb = new StringBuilder();
        sb.Append("## Merge group `").Append(mgPath).Append("` for root #").Append(rootId).Append("\n\n");
        sb.Append("Promotes `").Append(headBranch).Append("` into `").Append(baseBranch).Append("`.\n\n");
        sb.Append("This PR was opened by `polyphony pr open-mg-ado`. The detailed body — including the manifest of items in this merge group — is composed by the orchestrating workflow when it has that context.\n");
        return sb.ToString();
    }

    private static void EmitOpenMgAdo(PrOpenMergeGroupAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenMergeGroupAdoResult));

    private static void EmitOpenMgAdoError(
        int rootId,
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
        EmitOpenMgAdo(new PrOpenMergeGroupAdoResult
        {
            RootId = rootId,
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
