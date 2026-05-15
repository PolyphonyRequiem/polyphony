using System.Net;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.AzureDevOps;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes an evidence branch
    /// into its parent feature branch (or main for orphan evidence) on
    /// Azure DevOps. ADO analogue of <c>polyphony pr open-evidence-pr</c>.
    ///
    /// <para>Default branch naming follows PR #2 (the evidence branch
    /// builder): non-orphan = <c>evidence/{apex}-{workItem}</c> over
    /// <c>feature/{apex}</c>; orphan = <c>evidence/{workItem}</c> over
    /// <c>main</c>. Per-PR overrides via <c>--head</c> / <c>--base-branch</c>
    /// are honored verbatim.</para>
    ///
    /// <para><b>Routing-style exit code</b> — always exits 0; consumers
    /// branch on <see cref="PrOpenEvidenceAdoResult.ErrorCode"/>.</para>
    /// </summary>
    /// <param name="organization">ADO organization name.</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name.</param>
    /// <param name="workItem">The actionable work-item id this evidence PR satisfies.</param>
    /// <param name="apexId">Optional run-root feature id. When omitted (or zero), defaults to <paramref name="workItem"/> (orphan evidence).</param>
    /// <param name="head">Optional head branch override.</param>
    /// <param name="baseBranch">Optional base branch override.</param>
    /// <param name="title">Optional PR title.</param>
    /// <param name="body">Optional PR body.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-evidence-ado")]
    [VerbResult(typeof(PrOpenEvidenceAdoResult))]
    public async Task<int> OpenEvidenceAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int workItem = RequiredInput.MissingInt,
        int apexId = 0,
        string head = "",
        string baseBranch = "",
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr open-evidence-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var slug = BuildAdoSlug(organization, project, repository);

        if (workItem <= 0)
        {
            EmitOpenEvidenceAdoError(workItem, apexId, organization, project, repository, slug,
                "invalid_argument", $"workItem must be positive (got {workItem})");
            return ExitCodes.Success;
        }
        if (apexId < 0)
        {
            EmitOpenEvidenceAdoError(workItem, apexId, organization, project, repository, slug,
                "invalid_argument", $"apexId must be non-negative (got {apexId})");
            return ExitCodes.Success;
        }

        var effectiveApex = apexId == 0 ? workItem : apexId;
        var isOrphan = effectiveApex == workItem;

        var headBranch = string.IsNullOrWhiteSpace(head)
            ? (isOrphan
                ? $"evidence/{workItem}"
                : $"evidence/{effectiveApex}-{workItem}")
            : head;

        var resolvedBase = string.IsNullOrWhiteSpace(baseBranch)
            ? (isOrphan ? "main" : $"feature/{effectiveApex}")
            : baseBranch;

        if (ado is null)
        {
            EmitOpenEvidenceAdoError(workItem, effectiveApex, organization, project, repository, slug,
                "ado_failed", "IAdoClient is not configured", headBranch, resolvedBase);
            return ExitCodes.Success;
        }

        try
        {
            var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", ct).ConfigureAwait(false);
            if (headRefs.Count == 0)
            {
                EmitOpenEvidenceAdoError(workItem, effectiveApex, organization, project, repository, slug,
                    "missing_head_branch", $"head branch '{headBranch}' does not exist on remote",
                    headBranch, resolvedBase);
                return ExitCodes.Success;
            }

            var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{resolvedBase}", ct).ConfigureAwait(false);
            if (baseRefs.Count == 0)
            {
                EmitOpenEvidenceAdoError(workItem, effectiveApex, organization, project, repository, slug,
                    "missing_base_branch", $"base branch '{resolvedBase}' does not exist on remote",
                    headBranch, resolvedBase);
                return ExitCodes.Success;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitOpenEvidenceAdoError(workItem, effectiveApex, organization, project, repository, slug,
                "ado_failed", $"git ls-remote failed: {ex.Message}", headBranch, resolvedBase);
            return ExitCodes.Success;
        }

        var prTitle = string.IsNullOrWhiteSpace(title)
            ? await ResolveEvidencePrTitleAsync(workItem, ct).ConfigureAwait(false)
            : title;
        var prBody = string.IsNullOrWhiteSpace(body)
            ? BuildDefaultEvidenceBody(workItem, effectiveApex, headBranch, resolvedBase)
            : body;

        try
        {
            var activePrs = await ado.ListPullRequestsAsync(
                organization, project, repository,
                AdoPullRequestStatus.Active, sourceBranch: headBranch, ct).ConfigureAwait(false);

            if (activePrs is null)
            {
                EmitOpenEvidenceAdoError(workItem, effectiveApex, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, resolvedBase);
                return ExitCodes.Success;
            }

            var expectedTargetRef = "refs/heads/" + resolvedBase;
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
                EmitOpenEvidenceAdo(new PrOpenEvidenceAdoResult
                {
                    WorkItemId = workItem,
                    ApexId = effectiveApex,
                    HeadBranch = headBranch,
                    BaseBranch = resolvedBase,
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

            var created = await ado.CreatePullRequestAsync(
                organization, project, repository,
                sourceBranch: headBranch,
                targetBranch: resolvedBase,
                title: prTitle,
                description: prBody,
                ct).ConfigureAwait(false);

            if (created is null)
            {
                EmitOpenEvidenceAdoError(workItem, effectiveApex, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, resolvedBase);
                return ExitCodes.Success;
            }

            EmitOpenEvidenceAdo(new PrOpenEvidenceAdoResult
            {
                WorkItemId = workItem,
                ApexId = effectiveApex,
                HeadBranch = headBranch,
                BaseBranch = resolvedBase,
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
            EmitOpenEvidenceAdoError(workItem, effectiveApex, organization, project, repository, slug,
                "no_pat", ex.Message, headBranch, resolvedBase);
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitOpenEvidenceAdoError(workItem, effectiveApex, organization, project, repository, slug,
                "ado_timeout", ex.Message, headBranch, resolvedBase);
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitOpenEvidenceAdoError(workItem, effectiveApex, organization, project, repository, slug,
                code, ex.Message, headBranch, resolvedBase);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitOpenEvidenceAdoError(workItem, effectiveApex, organization, project, repository, slug,
                "ado_failed", ex.Message, headBranch, resolvedBase);
            return ExitCodes.Success;
        }
    }

    private static void EmitOpenEvidenceAdo(PrOpenEvidenceAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenEvidenceAdoResult));

    private static void EmitOpenEvidenceAdoError(
        int workItem,
        int apexId,
        string organization,
        string project,
        string repository,
        string slug,
        string errorCode,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitOpenEvidenceAdo(new PrOpenEvidenceAdoResult
        {
            WorkItemId = workItem,
            ApexId = apexId,
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
