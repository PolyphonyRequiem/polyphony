using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes an evidence branch
    /// into its parent feature branch (the run-root <c>feature/&lt;root&gt;</c>
    /// trunk), or into <c>main</c> for the orphan-evidence case where no
    /// root is supplied. Reuses an existing open PR for the same head/base
    /// pair instead of creating a duplicate.
    /// </summary>
    /// <remarks>
    /// Default branch naming follows Phase 6 PR #2 (the evidence branch
    /// builder):
    /// <list type="bullet">
    ///   <item>root differs from work item (the normal case): head =
    ///     <c>evidence/&lt;root&gt;-&lt;workItem&gt;</c>, base =
    ///     <c>feature/&lt;root&gt;</c>.</item>
    ///   <item>root omitted or equal to work item (orphan evidence): head =
    ///     <c>evidence/&lt;workItem&gt;</c>, base = <c>main</c>.</item>
    /// </list>
    /// Per-PR overrides via <c>--head</c> / <c>--base-branch</c> are honored
    /// verbatim. Title/body default to a deterministic stub composed from
    /// twig (the work item title); explicit overrides via <c>--title</c> /
    /// <c>--body</c> bypass twig entirely.
    /// </remarks>
    /// <param name="workItem">The actionable work-item id this evidence PR satisfies.</param>
    /// <param name="rootId">Optional run-root feature id. When omitted (or zero), defaults to <paramref name="workItem"/> (orphan evidence).</param>
    /// <param name="head">Optional head branch override. Defaults to the canonical evidence-branch name above.</param>
    /// <param name="baseBranch">Optional base branch override. Defaults to <c>feature/&lt;root&gt;</c>, or <c>main</c> in the orphan case.</param>
    /// <param name="title">Optional PR title; deterministic fallback derived from the work item's twig title when empty.</param>
    /// <param name="body">Optional PR body; minimal placeholder stub used when empty.</param>
    /// <param name="platform">Platform override (<c>github</c>|<c>ado</c>). Empty for origin-URL auto-detect.</param>
    /// <param name="organization">ADO organization. Required when platform=ado.</param>
    /// <param name="project">ADO project. Required when platform=ado.</param>
    /// <param name="repository">For ADO: repository name/GUID. For GitHub: <c>owner/name</c> slug. Required when <paramref name="platform"/> is non-empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-evidence-pr")]
    [JournaledAction(Action = "pr_open_evidence_pr")]
    [MutatesResource(ResourceKind.GitHubPr)]
    [VerbResult(typeof(PrOpenEvidenceResult))]
    public async Task<int> OpenEvidencePr(
        int workItem = RequiredInput.MissingInt,
        int rootId = 0,
        string head = "",
        string baseBranch = "",
        string title = "",
        string body = "",
        string platform = "",
        string organization = "",
        string project = "",
        string repository = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr open-evidence-pr",
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (workItem <= 0)
        {
            EmitEvidenceError(workItem, rootId, $"workItem must be positive (got {workItem})");
            return ExitCodes.ConfigError;
        }

        if (rootId < 0)
        {
            EmitEvidenceError(workItem, rootId, $"rootId must be non-negative (got {rootId})");
            return ExitCodes.ConfigError;
        }

        var effectiveRoot = rootId == 0 ? workItem : rootId;
        var isOrphan = effectiveRoot == workItem;

        var headBranch = string.IsNullOrWhiteSpace(head)
            ? (isOrphan
                ? $"evidence/{workItem}"
                : $"evidence/{effectiveRoot}-{workItem}")
            : head;

        var resolvedBase = string.IsNullOrWhiteSpace(baseBranch)
            ? (isOrphan ? "main" : $"feature/{effectiveRoot}")
            : baseBranch;
        PrOpenEvidencePrPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("pr_open_evidence_pr", BranchPairJournalTarget(headBranch, resolvedBase), effectiveRoot, workItem),
            async innerCt =>
            {
                var resolved = await repoIdentityResolver
                    .ResolveAsync(platform, organization, project, repository, innerCt)
                    .ConfigureAwait(false);

                if (resolved.Identity is Polyphony.Sdlc.Observers.RepoIdentity.AdoRepo adoRepo)
                {
                    var slug = BuildAdoSlug(adoRepo.Organization, adoRepo.Project, adoRepo.Repository);
                    var outcome = await OpenEvidenceAdoCoreAsync(
                        adoRepo.Organization, adoRepo.Project, adoRepo.Repository, slug,
                        workItem, effectiveRoot,
                        headBranch, resolvedBase,
                        title, body, innerCt).ConfigureAwait(false);

                    var result = new PrOpenEvidenceResult
                    {
                        PrNumber = outcome.PrNumber,
                        PrUrl = outcome.PrUrl,
                        Title = outcome.Title,
                        HeadBranch = outcome.HeadBranch,
                        BaseBranch = outcome.BaseBranch,
                        WorkItemId = workItem,
                        RootId = effectiveRoot,
                        Created = outcome.Created,
                        Organization = adoRepo.Organization,
                        Project = adoRepo.Project,
                        Repository = adoRepo.Repository,
                        RepoSlug = slug,
                        Error = outcome.Error,
                    };
                    payload = new PrOpenEvidencePrPayload
                    {
                        WorkItemId = workItem,
                        RootId = effectiveRoot,
                        HeadBranch = result.HeadBranch,
                        BaseBranch = result.BaseBranch,
                        RepoSlug = slug,
                        Organization = adoRepo.Organization,
                        Project = adoRepo.Project,
                        Repository = adoRepo.Repository,
                        PrNumber = result.PrNumber,
                        PrUrl = result.PrUrl,
                        Title = result.Title,
                        ResultAction = result.Error is null ? (result.Created ? "created" : "reused_existing_pr") : "error",
                        Succeeded = result.Error is null,
                        WasMutated = result.Created,
                        Error = result.Error,
                    };
                    EmitEvidence(result);
                    return outcome.Error is null ? ExitCodes.Success : ExitCodes.RoutingFailure;
                }

                try
                {
                    var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", innerCt).ConfigureAwait(false);
                    if (headRefs.Count == 0)
                    {
                        var error = $"head branch '{headBranch}' does not exist on remote";
                        payload = new PrOpenEvidencePrPayload
                        {
                            WorkItemId = workItem,
                            RootId = effectiveRoot,
                            HeadBranch = headBranch,
                            BaseBranch = resolvedBase,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitEvidenceError(workItem, effectiveRoot, error, headBranch: headBranch, baseBranch: resolvedBase);
                        return ExitCodes.RoutingFailure;
                    }

                    var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{resolvedBase}", innerCt).ConfigureAwait(false);
                    if (baseRefs.Count == 0)
                    {
                        var error = $"base branch '{resolvedBase}' does not exist on remote";
                        payload = new PrOpenEvidencePrPayload
                        {
                            WorkItemId = workItem,
                            RootId = effectiveRoot,
                            HeadBranch = headBranch,
                            BaseBranch = resolvedBase,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitEvidenceError(workItem, effectiveRoot, error, headBranch: headBranch, baseBranch: resolvedBase);
                        return ExitCodes.RoutingFailure;
                    }

                    var slug = await TryResolveSlugAsync(innerCt).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(slug))
                    {
                        const string error = "Could not resolve repo slug from origin remote";
                        payload = new PrOpenEvidencePrPayload
                        {
                            WorkItemId = workItem,
                            RootId = effectiveRoot,
                            HeadBranch = headBranch,
                            BaseBranch = resolvedBase,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitEvidenceError(workItem, effectiveRoot, error, headBranch: headBranch, baseBranch: resolvedBase);
                        return ExitCodes.RoutingFailure;
                    }

                    var prTitle = string.IsNullOrWhiteSpace(title)
                        ? await ResolveEvidencePrTitleAsync(workItem, innerCt).ConfigureAwait(false)
                        : title;
                    var prBody = string.IsNullOrWhiteSpace(body)
                        ? BuildDefaultEvidenceBody(workItem, effectiveRoot, headBranch, resolvedBase)
                        : body;

                    var existing = await gh.ListPullRequestsAsync(
                        slug,
                        new PrListFilters(Head: headBranch, Base: resolvedBase, State: "open", Limit: 1),
                        innerCt).ConfigureAwait(false);
                    if (existing.Count > 0)
                    {
                        var found = existing[0];
                        var result = new PrOpenEvidenceResult
                        {
                            PrNumber = found.Number,
                            PrUrl = found.Url ?? "",
                            Title = prTitle,
                            HeadBranch = headBranch,
                            BaseBranch = resolvedBase,
                            WorkItemId = workItem,
                            RootId = effectiveRoot,
                            Created = false,
                        };
                        payload = new PrOpenEvidencePrPayload
                        {
                            WorkItemId = workItem,
                            RootId = effectiveRoot,
                            HeadBranch = headBranch,
                            BaseBranch = resolvedBase,
                            RepoSlug = slug,
                            PrNumber = result.PrNumber,
                            PrUrl = result.PrUrl,
                            Title = result.Title,
                            ResultAction = "reused_existing_pr",
                            Succeeded = true,
                            WasMutated = false,
                        };
                        EmitEvidence(result);
                        return ExitCodes.Success;
                    }

                    var url = await gh.CreatePullRequestAsync(slug, resolvedBase, headBranch, prTitle, prBody, innerCt).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        const string error = "gh pr create failed — no URL returned";
                        payload = new PrOpenEvidencePrPayload
                        {
                            WorkItemId = workItem,
                            RootId = effectiveRoot,
                            HeadBranch = headBranch,
                            BaseBranch = resolvedBase,
                            RepoSlug = slug,
                            Title = prTitle,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitEvidenceError(workItem, effectiveRoot, error, headBranch: headBranch, baseBranch: resolvedBase);
                        return ExitCodes.RoutingFailure;
                    }

                    var trimmedUrl = url.Trim();
                    var createdResult = new PrOpenEvidenceResult
                    {
                        PrNumber = ExtractPrNumber(trimmedUrl),
                        PrUrl = trimmedUrl,
                        Title = prTitle,
                        HeadBranch = headBranch,
                        BaseBranch = resolvedBase,
                        WorkItemId = workItem,
                        RootId = effectiveRoot,
                        Created = true,
                    };
                    payload = new PrOpenEvidencePrPayload
                    {
                        WorkItemId = workItem,
                        RootId = effectiveRoot,
                        HeadBranch = headBranch,
                        BaseBranch = resolvedBase,
                        RepoSlug = slug,
                        PrNumber = createdResult.PrNumber,
                        PrUrl = createdResult.PrUrl,
                        Title = createdResult.Title,
                        ResultAction = "created",
                        Succeeded = true,
                        WasMutated = true,
                    };
                    EmitEvidence(createdResult);
                    return ExitCodes.Success;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    payload = new PrOpenEvidencePrPayload
                    {
                        WorkItemId = workItem,
                        RootId = effectiveRoot,
                        HeadBranch = headBranch,
                        BaseBranch = resolvedBase,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        Error = ex.Message,
                    };
                    EmitEvidenceError(workItem, effectiveRoot, ex.Message, headBranch: headBranch, baseBranch: resolvedBase);
                    return ExitCodes.RoutingFailure;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.PrOpenEvidencePrPayload),
            effectsSelector: _ => SelectOpenEvidencePrEffects(payload),
            ct: ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveEvidencePrTitleAsync(int workItem, CancellationToken ct)
    {
        var fallback = $"Evidence for #{workItem}";
        try
        {
            var tree = await twig.ShowTreeAsync(workItem, ct).ConfigureAwait(false);
            var workItemTitle = tree?["title"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(workItemTitle)
                ? fallback
                : $"Evidence: {workItemTitle} (#{workItem})";
        }
        catch
        {
            return fallback;
        }
    }

    private static string BuildDefaultEvidenceBody(int workItem, int rootId, string headBranch, string baseBranch)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## Evidence for #").Append(workItem);
        if (rootId != workItem)
        {
            sb.Append(" (root feature #").Append(rootId).Append(')');
        }
        sb.Append("\n\n");
        sb.Append("Promotes `").Append(headBranch).Append("` into `").Append(baseBranch).Append("`.\n\n");
        sb.Append("Work item: AB#").Append(workItem).Append("\n\n");
        sb.Append("### Evidence\n\n");
        sb.Append("<!-- Replace this stub with the artifacts that justify closing the work item.\n");
        sb.Append("     Free-form: links, notes, transcripts, screenshots, decision rationale.\n");
        sb.Append("     The plan reviewer judges sufficiency; there is no required schema. -->\n");
        return sb.ToString();
    }

    private static void EmitEvidence(PrOpenEvidenceResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenEvidenceResult));

    private static void EmitEvidenceError(
        int workItem,
        int rootId,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitEvidence(new PrOpenEvidenceResult
        {
            PrNumber = 0,
            PrUrl = "",
            Title = "",
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            WorkItemId = workItem,
            RootId = rootId == 0 ? workItem : rootId,
            Created = false,
            Error = message,
        });
    }
}
