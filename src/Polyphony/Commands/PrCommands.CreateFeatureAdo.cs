using System.Net;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.AzureDevOps.Auth;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;

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
    /// <param name="rootId">Root work-item id of the run's root (focus) item.</param>
    /// <param name="targetBranch">Target branch (typically <c>main</c>); defaults to <c>main</c>.</param>
    /// <param name="title">Optional PR title; deterministic fallback (derived from the work-item title via twig) used when empty.</param>
    /// <param name="body">Optional PR body; minimal deterministic fallback used when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("create-feature-ado")]
    [JournaledAction(Action = "pr_create_feature_ado")]
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
        PrCreateFeatureAdoPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation(
                "pr_create_feature_ado",
                BranchPairJournalTarget(headBranch, baseBranch),
                rootId,
                rootId),
            async innerCt =>
            {
                if (ado is null)
                {
                    const string error = "IAdoClient is not configured";
                    payload = new PrCreateFeatureAdoPayload
                    {
                        RootId = rootId,
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RepoSlug = slug,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = "ado_failed",
                        Error = error,
                    };
                    EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                        "ado_failed", error, headBranch, baseBranch);
                    return ExitCodes.Success;
                }

                try
                {
                    var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", innerCt).ConfigureAwait(false);
                    if (headRefs.Count == 0)
                    {
                        var error = $"head branch '{headBranch}' does not exist on remote";
                        payload = new PrCreateFeatureAdoPayload
                        {
                            RootId = rootId,
                            Organization = organization,
                            Project = project,
                            Repository = repository,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RepoSlug = slug,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            ErrorCode = "missing_head_branch",
                            Error = error,
                        };
                        EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                            "missing_head_branch", error, headBranch, baseBranch);
                        return ExitCodes.Success;
                    }

                    var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{baseBranch}", innerCt).ConfigureAwait(false);
                    if (baseRefs.Count == 0)
                    {
                        var error = $"base branch '{baseBranch}' does not exist on remote";
                        payload = new PrCreateFeatureAdoPayload
                        {
                            RootId = rootId,
                            Organization = organization,
                            Project = project,
                            Repository = repository,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RepoSlug = slug,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            ErrorCode = "missing_base_branch",
                            Error = error,
                        };
                        EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                            "missing_base_branch", error, headBranch, baseBranch);
                        return ExitCodes.Success;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    payload = new PrCreateFeatureAdoPayload
                    {
                        RootId = rootId,
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RepoSlug = slug,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = "ado_failed",
                        Error = $"git ls-remote failed: {ex.Message}",
                    };
                    EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                        "ado_failed", $"git ls-remote failed: {ex.Message}", headBranch, baseBranch);
                    return ExitCodes.Success;
                }

                var prTitle = string.IsNullOrWhiteSpace(title)
                    ? await ResolvePrTitleAsync(rootId, innerCt).ConfigureAwait(false)
                    : title;
                var prBody = string.IsNullOrWhiteSpace(body)
                    ? await BuildPrBodyAsync(rootId, headBranch, baseBranch, innerCt).ConfigureAwait(false)
                    : body;

                try
                {
                    var allPrs = await ado.ListPullRequestsAsync(
                        organization, project, repository,
                        AdoPullRequestStatus.All, null, innerCt).ConfigureAwait(false);

                    if (allPrs is null)
                    {
                        var error = $"Repository '{repository}' not found in {organization}/{project}.";
                        payload = new PrCreateFeatureAdoPayload
                        {
                            RootId = rootId,
                            Organization = organization,
                            Project = project,
                            Repository = repository,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RepoSlug = slug,
                            Title = prTitle,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            ErrorCode = "pr_not_found",
                            Error = error,
                        };
                        EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                            "pr_not_found", error, headBranch, baseBranch);
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
                            completedMatch = pr;
                        }
                    }

                    if (activeMatch is null && completedMatch is not null)
                    {
                        var validity = await ValidateCompletedAdoPrAsync(
                            organization, project, repository,
                            completedMatch.PullRequestId, headBranch, baseBranch, innerCt).ConfigureAwait(false);
                        if (!validity.IsValid) completedMatch = null;
                    }

                    var existing = activeMatch ?? completedMatch;

                    if (existing is not null)
                    {
                        var result = new PrCreateFeatureAdoResult
                        {
                            RootId = rootId,
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
                        };
                        payload = new PrCreateFeatureAdoPayload
                        {
                            RootId = rootId,
                            Organization = organization,
                            Project = project,
                            Repository = repository,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RepoSlug = slug,
                            PrNumber = result.PrNumber,
                            PrUrl = result.PrUrl,
                            Title = result.Title,
                            ResultAction = "reused_existing_pr",
                            Succeeded = true,
                            WasMutated = false,
                            ErrorCode = result.ErrorCode,
                        };
                        EmitCreateFeatureAdo(result);
                        return ExitCodes.Success;
                    }

                    var created = await ado.CreatePullRequestAsync(
                        organization, project, repository,
                        sourceBranch: headBranch,
                        targetBranch: baseBranch,
                        title: prTitle,
                        description: prBody,
                        innerCt).ConfigureAwait(false);

                    if (created is null)
                    {
                        var error = $"Repository '{repository}' not found in {organization}/{project}.";
                        payload = new PrCreateFeatureAdoPayload
                        {
                            RootId = rootId,
                            Organization = organization,
                            Project = project,
                            Repository = repository,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RepoSlug = slug,
                            Title = prTitle,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            ErrorCode = "pr_not_found",
                            Error = error,
                        };
                        EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                            "pr_not_found", error, headBranch, baseBranch);
                        return ExitCodes.Success;
                    }

                    var createdResult = new PrCreateFeatureAdoResult
                    {
                        RootId = rootId,
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
                    };
                    payload = new PrCreateFeatureAdoPayload
                    {
                        RootId = rootId,
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RepoSlug = slug,
                        PrNumber = createdResult.PrNumber,
                        PrUrl = createdResult.PrUrl,
                        Title = createdResult.Title,
                        ResultAction = "created",
                        Succeeded = true,
                        WasMutated = true,
                        ErrorCode = createdResult.ErrorCode,
                    };
                    EmitCreateFeatureAdo(createdResult);
                    return ExitCodes.Success;
                }
                catch (OperationCanceledException) { throw; }
                catch (AdoAuthenticationException ex)
                {
                    payload = new PrCreateFeatureAdoPayload
                    {
                        RootId = rootId,
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RepoSlug = slug,
                        Title = prTitle,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = "no_pat",
                        Error = ex.Message,
                    };
                    EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                        "no_pat", ex.Message, headBranch, baseBranch);
                    return ExitCodes.Success;
                }
                catch (TimeoutException ex)
                {
                    payload = new PrCreateFeatureAdoPayload
                    {
                        RootId = rootId,
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RepoSlug = slug,
                        Title = prTitle,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = "ado_timeout",
                        Error = ex.Message,
                    };
                    EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                        "ado_timeout", ex.Message, headBranch, baseBranch);
                    return ExitCodes.Success;
                }
                catch (HttpRequestException ex)
                {
                    var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "no_pat"
                        : "ado_failed";
                    payload = new PrCreateFeatureAdoPayload
                    {
                        RootId = rootId,
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RepoSlug = slug,
                        Title = prTitle,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = code,
                        Error = ex.Message,
                    };
                    EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                        code, ex.Message, headBranch, baseBranch);
                    return ExitCodes.Success;
                }
                catch (Exception ex)
                {
                    payload = new PrCreateFeatureAdoPayload
                    {
                        RootId = rootId,
                        Organization = organization,
                        Project = project,
                        Repository = repository,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RepoSlug = slug,
                        Title = prTitle,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        ErrorCode = "ado_failed",
                        Error = ex.Message,
                    };
                    EmitCreateFeatureAdoError(rootId, targetBranch, organization, project, repository, slug,
                        "ado_failed", ex.Message, headBranch, baseBranch);
                    return ExitCodes.Success;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.PrCreateFeatureAdoPayload),
            ct: ct).ConfigureAwait(false);
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
