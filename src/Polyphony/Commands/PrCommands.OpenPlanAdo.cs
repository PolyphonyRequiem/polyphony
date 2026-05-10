using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes a plan branch into
    /// its parent plan branch (or the feature branch for the root plan)
    /// on Azure DevOps. ADO analogue of <c>polyphony pr open-plan-pr</c>.
    ///
    /// <para>The verb shape is identical to the GitHub-side equivalent:
    /// derive head/base from the plan-tree position, read the manifest
    /// from <c>origin/feature/{root}</c> via <c>git show</c>, compute the
    /// <c>ancestor_plan_generations</c> snapshot, embed it in the PR body
    /// as YAML front-matter, then either reuse an existing OPEN PR with a
    /// matching snapshot (idempotent) or create a fresh PR. The same
    /// front-matter parser (<see cref="PlanPrFrontMatter"/>) reads the
    /// embedded snapshot back at merge time — workflow consumers branch on
    /// the same <c>ancestor_plan_generations</c> shape regardless of
    /// platform.</para>
    ///
    /// <para><b>Routing-style exit code</b> — always exits 0; consumers
    /// branch on <see cref="PrOpenPlanAdoResult.ErrorCode"/>. This
    /// matches the other ADO-side verbs (<c>vote-ado</c>,
    /// <c>poll-status-ado</c>) and contrasts with the GitHub-side
    /// <c>open-plan-pr</c> which uses categorical exit codes.</para>
    ///
    /// <para><b>Reuse semantics.</b> ADO's
    /// <see cref="IAdoClient.ListPullRequestsAsync"/> currently only
    /// filters by status; the verb client-side filters the active PR list
    /// by source-ref + target-ref to find the candidate for reuse. Body
    /// is fetched via the second-call <see cref="IAdoClient.GetPullRequestPollDataAsync"/>
    /// — which already composes the body from the PR detail endpoint.</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name; both accepted.</param>
    /// <param name="rootId">ADO work-item id of the run's root (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the item this plan PR belongs to. Equal to <paramref name="rootId"/> for the root plan.</param>
    /// <param name="parentItemId">Immediate plan-tree parent's work-item id. Required for descendants of descendants; omit for root plan and direct children of root plan.</param>
    /// <param name="ancestorIds">Comma-separated ancestor chain (immediate parent first), used to compute the snapshot. For a child of root: <c>"root"</c>. For a deeper descendant: e.g. <c>"5678,root"</c>. Empty for the root plan.</param>
    /// <param name="manifestPath">Path to the run manifest within the <c>origin/feature/{root}</c> blob. Defaults to <c>.polyphony/run.yaml</c>.</param>
    /// <param name="title">Optional PR title; deterministic fallback derived from the cached work-item title.</param>
    /// <param name="body">Optional PR body summary (rendered after the front-matter); minimal deterministic fallback used when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-plan-ado")]
    [VerbResult(typeof(PrOpenPlanAdoResult))]
    public async Task<int> OpenPlanAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        int parentItemId = 0,
        string ancestorIds = "",
        string manifestPath = "",
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr open-plan-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt)) is { } halt)
            return halt;
        var slug = BuildAdoSlug(organization, project, repository);

        // ── 1. Validate inputs. ────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository))
        {
            EmitOpenPlanAdoError(rootId, itemId, parentItemId, organization, project, repository, slug,
                "invalid_argument", "organization, project, and repository are required");
            return ExitCodes.Success;
        }
        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitOpenPlanAdoError(rootId, itemId, parentItemId, organization, project, repository, slug,
                "invalid_argument", $"rootId must be positive (got {rootId})");
            return ExitCodes.Success;
        }
        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitOpenPlanAdoError(rootId, itemId, parentItemId, organization, project, repository, slug,
                "invalid_argument", $"itemId must be positive (got {itemId})");
            return ExitCodes.Success;
        }

        bool isRootPlan = itemId == rootId;
        string itemKey;
        string headBranch;
        string baseBranch;
        int resolvedParent = 0;

        if (isRootPlan)
        {
            if (parentItemId != 0)
            {
                EmitOpenPlanAdoError(rootId, itemId, parentItemId, organization, project, repository, slug,
                    "invalid_argument",
                    $"--parent-item-id must not be provided when --item-id == --root-id (got {parentItemId}); the root plan has no parent.");
                return ExitCodes.Success;
            }
            itemKey = "root";
            headBranch = BranchNameBuilder.RootPlan(root).Value;
            baseBranch = BranchNameBuilder.Feature(root).Value;
        }
        else
        {
            if (parentItemId == 0)
            {
                headBranch = BranchNameBuilder.DescendantPlan(root, item).Value;
                baseBranch = BranchNameBuilder.RootPlan(root).Value;
            }
            else
            {
                if (!WorkItemId.TryParse(parentItemId, out var parentItem))
                {
                    EmitOpenPlanAdoError(rootId, itemId, parentItemId, organization, project, repository, slug,
                        "invalid_argument", $"--parent-item-id must be positive (got {parentItemId})");
                    return ExitCodes.Success;
                }
                if (parentItemId == itemId)
                {
                    EmitOpenPlanAdoError(rootId, itemId, parentItemId, organization, project, repository, slug,
                        "invalid_argument",
                        $"--parent-item-id ({parentItemId}) must not equal --item-id; a plan cannot be its own parent.");
                    return ExitCodes.Success;
                }
                if (parentItemId == rootId)
                {
                    EmitOpenPlanAdoError(rootId, itemId, parentItemId, organization, project, repository, slug,
                        "invalid_argument",
                        $"--parent-item-id ({parentItemId}) equals --root-id; omit --parent-item-id when the parent is the root plan.");
                    return ExitCodes.Success;
                }
                resolvedParent = parentItemId;
                headBranch = BranchNameBuilder.DescendantPlan(root, item).Value;
                baseBranch = BranchNameBuilder.DescendantPlan(root, parentItem).Value;
            }
            itemKey = itemId.ToString(CultureInfo.InvariantCulture);
        }

        if (!TryParseAncestorChain(ancestorIds, isRootPlan, itemKey, out var ancestorKeys, out var ancestorError))
        {
            EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
                "invalid_argument", ancestorError, headBranch, baseBranch);
            return ExitCodes.Success;
        }

        if (ado is null)
        {
            // Shouldn't happen in production (DI registers IAdoClient) but the
            // ctor allows null so unit tests can opt out of the ADO leg.
            EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
                "ado_failed", "IAdoClient is not configured", headBranch, baseBranch);
            return ExitCodes.Success;
        }

        // ── 2. Read manifest + compute snapshot. ───────────────────────────
        // Rev 4.2: the manifest lives locally under the git common dir;
        // it is the single source of truth, mutated under the run lock by
        // `pr merge-plan-pr`/`merge-plan-ado` and the rebase/recreate verbs.
        // No git fetch/show needed.
        IReadOnlyDictionary<string, int> snapshot;
        var featureBranch = BranchNameBuilder.Feature(root).Value;
        var resolvedPath = await ManifestPathHelper.ResolveAsync(statePaths, rootId, manifestPath, ct).ConfigureAwait(false);
        if (resolvedPath.Error is not null)
        {
            EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
                "manifest_read_failed", resolvedPath.Error, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        var localManifestPath = resolvedPath.Path;
        try
        {
            var manifest = RunManifestStore.LoadOrThrow(localManifestPath);
            snapshot = ComputeSnapshot(manifest.PlanGenerations, ancestorKeys);
        }
        catch (FileNotFoundException)
        {
            EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
                "manifest_read_failed",
                $"manifest not found at {localManifestPath} — run `polyphony manifest init --root-id {rootId} ...` first",
                headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
                "manifest_invalid", $"manifest invalid: {ex.Message}", headBranch, baseBranch);
            return ExitCodes.Success;
        }

        // ── 3. Build PR title + body. ──────────────────────────────────────
        var prTitle = string.IsNullOrWhiteSpace(title)
            ? await ResolvePlanPrTitleAsync(itemId, isRootPlan, ct).ConfigureAwait(false)
            : title;
        var summaryBody = string.IsNullOrWhiteSpace(body)
            ? BuildDefaultPlanBodySummary(rootId, itemId, isRootPlan, headBranch, baseBranch)
            : body;
        var fullBody = BuildPlanPrBody(snapshot, summaryBody);

        try
        {
            // ── 4. Reuse check: scan active PRs for a matching head/base. ─
            var activePrs = await ado.ListPullRequestsAsync(
                organization, project, repository,
                AdoPullRequestStatus.Active, ct).ConfigureAwait(false);

            if (activePrs is null)
            {
                EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
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
                // Re-read the body via the poll-data composer — the PR list
                // returns the description but the poll-data path is the
                // canonical channel for body content (matches what the merge
                // verb will use to read front-matter).
                AdoPullRequestPollData? pollData = null;
                try
                {
                    pollData = await ado.GetPullRequestPollDataAsync(
                        organization, project, repository, existing.PullRequestId, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best-effort body fetch — fall through using the list-returned description below.
                }
                var existingBody = pollData?.Body ?? existing.Description;
                var existingMeta = string.IsNullOrEmpty(existingBody)
                    ? new PrPollMetadata
                    {
                        RequestsParentChange = false,
                        AncestorPlanGenerations = new Dictionary<string, int>(StringComparer.Ordinal),
                    }
                    : PlanPrFrontMatter.Parse(existingBody);

                if (SnapshotsEquivalent(existingMeta.AncestorPlanGenerations, snapshot))
                {
                    EmitOpenPlanAdo(new PrOpenPlanAdoResult
                    {
                        RootId = rootId,
                        ItemId = itemId,
                        ParentItemId = resolvedParent,
                        ItemKey = itemKey,
                        IsRootPlan = isRootPlan,
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
                        Stale = false,
                        RequestsParentChange = existingMeta.RequestsParentChange,
                        AncestorPlanGenerations = existingMeta.AncestorPlanGenerations,
                        ErrorCode = "",
                    });
                    return ExitCodes.Success;
                }

                EmitOpenPlanAdo(new PrOpenPlanAdoResult
                {
                    RootId = rootId,
                    ItemId = itemId,
                    ParentItemId = resolvedParent,
                    ItemKey = itemKey,
                    IsRootPlan = isRootPlan,
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
                    Stale = true,
                    RequestsParentChange = existingMeta.RequestsParentChange,
                    AncestorPlanGenerations = existingMeta.AncestorPlanGenerations,
                    ErrorCode = "stale_metadata",
                    Error = BuildStaleMessage(existingMeta.AncestorPlanGenerations, snapshot),
                });
                return ExitCodes.Success;
            }

            // ── 5. Create the PR. ─────────────────────────────────────────
            var created = await ado.CreatePullRequestAsync(
                organization, project, repository,
                sourceBranch: headBranch,
                targetBranch: baseBranch,
                title: prTitle,
                description: fullBody,
                ct).ConfigureAwait(false);

            if (created is null)
            {
                EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.",
                    headBranch, baseBranch);
                return ExitCodes.Success;
            }

            EmitOpenPlanAdo(new PrOpenPlanAdoResult
            {
                RootId = rootId,
                ItemId = itemId,
                ParentItemId = resolvedParent,
                ItemKey = itemKey,
                IsRootPlan = isRootPlan,
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
                Stale = false,
                RequestsParentChange = false,
                AncestorPlanGenerations = snapshot,
                ErrorCode = "",
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            // Raised by AdoClient.ResolvePatOrThrow when no PAT is configured.
            EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
                "no_pat", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
                "ado_timeout", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (HttpRequestException ex)
        {
            // 401/403 → no_pat (PAT is missing or rejected); everything else → ado_failed.
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
                code, ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitOpenPlanAdoError(rootId, itemId, resolvedParent, organization, project, repository, slug,
                "ado_failed", ex.Message, headBranch, baseBranch);
            return ExitCodes.Success;
        }
    }

    private static void EmitOpenPlanAdo(PrOpenPlanAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenPlanAdoResult));

    private static void EmitOpenPlanAdoError(
        int rootId,
        int itemId,
        int parentItemId,
        string organization,
        string project,
        string repository,
        string slug,
        string errorCode,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        var itemKey = itemId == rootId
            ? "root"
            : (itemId > 0 ? itemId.ToString(CultureInfo.InvariantCulture) : "");
        EmitOpenPlanAdo(new PrOpenPlanAdoResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            ItemKey = itemKey,
            IsRootPlan = itemId == rootId && itemId > 0,
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
            Stale = false,
            RequestsParentChange = false,
            AncestorPlanGenerations = new Dictionary<string, int>(StringComparer.Ordinal),
            ErrorCode = errorCode,
            Error = message,
        });
    }
}
