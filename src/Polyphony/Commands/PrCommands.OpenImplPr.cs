using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes a impl branch into
    /// its enclosing merge-group branch. Head is <c>impl/{root_id}-{item_id}</c>;
    /// base is <c>mg/{root_id}_{mg_path}</c>. Reuses an existing open PR
    /// for the same head/base pair instead of creating a duplicate.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's root (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the task.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path of the enclosing MG.</param>
    /// <param name="title">Optional PR title; deterministic fallback derived from the cached work-item title.</param>
    /// <param name="body">Optional PR body; minimal deterministic fallback used when empty.</param>
    /// <param name="platform">Platform override (<c>github</c>|<c>ado</c>). Empty for origin-URL auto-detect.</param>
    /// <param name="organization">ADO organization. Required when platform=ado.</param>
    /// <param name="project">ADO project. Required when platform=ado.</param>
    /// <param name="repository">For ADO: repository name/GUID. For GitHub: <c>owner/name</c> slug. Required when <paramref name="platform"/> is non-empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-impl-pr")]
    [JournaledAction(Action = "pr_open_impl_pr")]
    [MutatesResource(ResourceKind.GitHubPr)]
    [VerbResult(typeof(PrOpenImplResult))]
    public async Task<int> OpenImplPr(
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        string mgPath = "",
        string title = "",
        string body = "",
        string platform = "",
        string organization = "",
        string project = "",
        string repository = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr open-impl-pr",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitImplError(rootId, itemId, mgPath, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }

        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitImplError(rootId, itemId, mgPath, $"itemId must be positive (got {itemId})");
            return ExitCodes.ConfigError;
        }

        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitImplError(
                rootId,
                itemId,
                mgPath,
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.ConfigError;
        }

        var headBranch = BranchNameBuilder.Impl(root, item).Value;
        var baseBranch = BranchNameBuilder.MergeGroup(root, path).Value;
        PrOpenImplPrPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("pr_open_impl_pr", BranchPairJournalTarget(headBranch, baseBranch), rootId, itemId),
            async innerCt =>
            {
                var resolved = await repoIdentityResolver
                    .ResolveAsync(platform, organization, project, repository, innerCt)
                    .ConfigureAwait(false);

                if (resolved.Identity is Polyphony.Sdlc.Observers.RepoIdentity.AdoRepo adoRepo)
                {
                    var adoSlug = BuildAdoSlug(adoRepo.Organization, adoRepo.Project, adoRepo.Repository);
                    var outcome = await OpenImplAdoCoreAsync(
                        adoRepo.Organization, adoRepo.Project, adoRepo.Repository, adoSlug,
                        rootId, itemId, path,
                        headBranch, baseBranch,
                        title, body, innerCt).ConfigureAwait(false);

                    var result = new PrOpenImplResult
                    {
                        PrNumber = outcome.PrNumber,
                        PrUrl = outcome.PrUrl,
                        Title = outcome.Title,
                        HeadBranch = outcome.HeadBranch,
                        BaseBranch = outcome.BaseBranch,
                        RootId = rootId,
                        ItemId = itemId,
                        MgPath = path.Canonical,
                        Created = outcome.Created,
                        Organization = adoRepo.Organization,
                        Project = adoRepo.Project,
                        Repository = adoRepo.Repository,
                        RepoSlug = adoSlug,
                        Error = outcome.Error,
                    };
                    payload = new PrOpenImplPrPayload
                    {
                        RootId = rootId,
                        ItemId = itemId,
                        MergeGroupPath = path.Canonical,
                        HeadBranch = result.HeadBranch,
                        BaseBranch = result.BaseBranch,
                        RepoSlug = adoSlug,
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
                    EmitImpl(result);
                    return outcome.Error is null ? ExitCodes.Success : ExitCodes.RoutingFailure;
                }

                try
                {
                    var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", innerCt).ConfigureAwait(false);
                    if (headRefs.Count == 0)
                    {
                        var error = $"head branch '{headBranch}' does not exist on remote";
                        payload = new PrOpenImplPrPayload
                        {
                            RootId = rootId,
                            ItemId = itemId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitImplError(rootId, itemId, mgPath, error, headBranch: headBranch, baseBranch: baseBranch);
                        return ExitCodes.RoutingFailure;
                    }

                    var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{baseBranch}", innerCt).ConfigureAwait(false);
                    if (baseRefs.Count == 0)
                    {
                        var error = $"base branch '{baseBranch}' does not exist on remote";
                        payload = new PrOpenImplPrPayload
                        {
                            RootId = rootId,
                            ItemId = itemId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitImplError(rootId, itemId, mgPath, error, headBranch: headBranch, baseBranch: baseBranch);
                        return ExitCodes.RoutingFailure;
                    }

                    var slug = await TryResolveSlugAsync(innerCt).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(slug))
                    {
                        const string error = "Could not resolve repo slug from origin remote";
                        payload = new PrOpenImplPrPayload
                        {
                            RootId = rootId,
                            ItemId = itemId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitImplError(rootId, itemId, mgPath, error, headBranch: headBranch, baseBranch: baseBranch);
                        return ExitCodes.RoutingFailure;
                    }

                    var prTitle = string.IsNullOrWhiteSpace(title)
                        ? await ResolveImplPrTitleAsync(itemId, innerCt).ConfigureAwait(false)
                        : title;
                    var rawBody = string.IsNullOrWhiteSpace(body)
                        ? BuildDefaultImplBody(rootId, itemId, path.Canonical, headBranch, baseBranch)
                        : body;
                    // W6 (AB#3280): stamp the run-id marker on the first
                    // line so cross-machine readers can ground the PR in
                    // a lineage when the local journal is silent.
                    var prBody = PrBodyMarker.EnsureRunIdPrefix(rawBody, _runContext.RunId);

                    var existing = await gh.ListPullRequestsAsync(
                        slug,
                        new PrListFilters(Head: headBranch, Base: baseBranch, State: "open", Limit: 1),
                        innerCt).ConfigureAwait(false);
                    if (existing.Count > 0)
                    {
                        var found = existing[0];

                        // W10 (AB#3291): refuse to adopt a foreign-lineage PR
                        // even if its head/base match — a leftover from a prior
                        // run that shares the canonical branch names must not
                        // be silently claimed by the current lineage.
                        var lineageReason = await CheckGhMergeLineageAsync(
                            slug, found.Number, bodyHasFrontMatter: false,
                            journalAction: "pr_open_impl_pr",
                            journalTarget: BranchPairJournalTarget(headBranch, baseBranch),
                            innerCt).ConfigureAwait(false);
                        if (lineageReason is not null)
                        {
                            payload = new PrOpenImplPrPayload
                            {
                                RootId = rootId,
                                ItemId = itemId,
                                MergeGroupPath = path.Canonical,
                                HeadBranch = headBranch,
                                BaseBranch = baseBranch,
                                RepoSlug = slug,
                                PrNumber = found.Number,
                                PrUrl = found.Url ?? "",
                                Title = prTitle,
                                ResultAction = "error",
                                Succeeded = false,
                                WasMutated = false,
                                Error = "foreign_lineage: " + lineageReason,
                            };
                            EmitImplError(rootId, itemId, mgPath, payload.Error, headBranch: headBranch, baseBranch: baseBranch);
                            return ExitCodes.RoutingFailure;
                        }

                        var result = new PrOpenImplResult
                        {
                            PrNumber = found.Number,
                            PrUrl = found.Url ?? "",
                            Title = prTitle,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RootId = rootId,
                            ItemId = itemId,
                            MgPath = path.Canonical,
                            Created = false,
                        };
                        payload = new PrOpenImplPrPayload
                        {
                            RootId = rootId,
                            ItemId = itemId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RepoSlug = slug,
                            PrNumber = result.PrNumber,
                            PrUrl = result.PrUrl,
                            Title = result.Title,
                            ResultAction = "reused_existing_pr",
                            Succeeded = true,
                            WasMutated = false,
                        };
                        EmitImpl(result);
                        return ExitCodes.Success;
                    }

                    var url = await gh.CreatePullRequestAsync(slug, baseBranch, headBranch, prTitle, prBody, innerCt).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        const string error = "gh pr create failed — no URL returned";
                        payload = new PrOpenImplPrPayload
                        {
                            RootId = rootId,
                            ItemId = itemId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RepoSlug = slug,
                            Title = prTitle,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitImplError(rootId, itemId, mgPath, error, headBranch: headBranch, baseBranch: baseBranch);
                        return ExitCodes.RoutingFailure;
                    }

                    var trimmedUrl = url.Trim();
                    var createdResult = new PrOpenImplResult
                    {
                        PrNumber = ExtractPrNumber(trimmedUrl),
                        PrUrl = trimmedUrl,
                        Title = prTitle,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RootId = rootId,
                        ItemId = itemId,
                        MgPath = path.Canonical,
                        Created = true,
                    };
                    payload = new PrOpenImplPrPayload
                    {
                        RootId = rootId,
                        ItemId = itemId,
                        MergeGroupPath = path.Canonical,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RepoSlug = slug,
                        PrNumber = createdResult.PrNumber,
                        PrUrl = createdResult.PrUrl,
                        Title = createdResult.Title,
                        ResultAction = "created",
                        Succeeded = true,
                        WasMutated = true,
                    };
                    EmitImpl(createdResult);
                    return ExitCodes.Success;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    payload = new PrOpenImplPrPayload
                    {
                        RootId = rootId,
                        ItemId = itemId,
                        MergeGroupPath = path.Canonical,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        Error = ex.Message,
                    };
                    EmitImplError(rootId, itemId, mgPath, ex.Message, headBranch: headBranch, baseBranch: baseBranch);
                    return ExitCodes.RoutingFailure;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.PrOpenImplPrPayload),
            effectsSelector: _ => SelectOpenImplPrEffects(payload),
            ct: ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveImplPrTitleAsync(int itemId, CancellationToken ct)
    {
        var fallback = $"impl #{itemId}";
        try
        {
            var tree = await twig.ShowTreeAsync(itemId, ct).ConfigureAwait(false);
            var workItemTitle = tree?["title"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(workItemTitle) ? fallback : $"{workItemTitle} AB#{itemId}";
        }
        catch
        {
            return fallback;
        }
    }

    private static string BuildDefaultImplBody(int rootId, int itemId, string mgPath, string headBranch, string baseBranch)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## Impl #").Append(itemId).Append(" for root #").Append(rootId).Append("\n\n");
        sb.Append("Promotes `").Append(headBranch).Append("` into merge group `").Append(mgPath)
          .Append("` (base `").Append(baseBranch).Append("`).\n\n");
        sb.Append("AB#").Append(itemId).Append('\n');
        return sb.ToString();
    }

    private static void EmitImpl(PrOpenImplResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenImplResult));

    private static void EmitImplError(
        int rootId,
        int itemId,
        string mgPath,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitImpl(new PrOpenImplResult
        {
            PrNumber = 0,
            PrUrl = "",
            Title = "",
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            RootId = rootId,
            ItemId = itemId,
            MgPath = mgPath,
            Created = false,
            Error = message,
        });
    }
}
