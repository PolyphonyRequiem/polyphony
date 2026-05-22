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
    /// Open (or reuse) the pull request that promotes a merge-group branch
    /// into its parent. Head is <c>mg/{root_id}_{mg_path}</c>; base is the
    /// parent merge-group branch when nested, or the feature branch when
    /// top-level. Reuses an existing open PR for the same head/base pair
    /// instead of creating a duplicate.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's root (focus) item.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path.</param>
    /// <param name="title">Optional PR title; deterministic fallback used when empty.</param>
    /// <param name="body">Optional PR body; minimal deterministic fallback used when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-mg-pr")]
    [JournaledAction(Action = "pr_open_mg_pr")]
    [MutatesResource(ResourceKind.GitHubPr)]
    [VerbResult(typeof(PrOpenMergeGroupResult))]
    public async Task<int> OpenMergeGroupPr(
        int rootId = RequiredInput.MissingInt,
        string mgPath = "",
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr open-mg-pr",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitMgError(rootId, mgPath, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }

        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitMgError(
                rootId,
                mgPath,
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.ConfigError;
        }

        var headBranch = BranchNameBuilder.MergeGroup(root, path).Value;
        var baseBranch = path.IsTopLevel
            ? BranchNameBuilder.Feature(root).Value
            : BranchNameBuilder.MergeGroup(root, MergeGroupPath.Of(path.Segments.Take(path.Depth - 1))).Value;
        PrOpenMergeGroupPrPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("pr_open_mg_pr", BranchPairJournalTarget(headBranch, baseBranch), rootId, rootId),
            async innerCt =>
            {
                try
                {
                    var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", innerCt).ConfigureAwait(false);
                    if (headRefs.Count == 0)
                    {
                        var error = $"head branch '{headBranch}' does not exist on remote";
                        payload = new PrOpenMergeGroupPrPayload
                        {
                            RootId = rootId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitMgError(rootId, mgPath, error, headBranch: headBranch, baseBranch: baseBranch);
                        return ExitCodes.RoutingFailure;
                    }

                    var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{baseBranch}", innerCt).ConfigureAwait(false);
                    if (baseRefs.Count == 0)
                    {
                        var error = $"base branch '{baseBranch}' does not exist on remote";
                        payload = new PrOpenMergeGroupPrPayload
                        {
                            RootId = rootId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitMgError(rootId, mgPath, error, headBranch: headBranch, baseBranch: baseBranch);
                        return ExitCodes.RoutingFailure;
                    }

                    var slug = await TryResolveSlugAsync(innerCt).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(slug))
                    {
                        const string error = "Could not resolve repo slug from origin remote";
                        payload = new PrOpenMergeGroupPrPayload
                        {
                            RootId = rootId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitMgError(rootId, mgPath, error, headBranch: headBranch, baseBranch: baseBranch);
                        return ExitCodes.RoutingFailure;
                    }

                    var prTitle = string.IsNullOrWhiteSpace(title)
                        ? $"merge group {path.Canonical} for root #{rootId}"
                        : title;
                    var prBody = string.IsNullOrWhiteSpace(body)
                        ? BuildDefaultMgBody(rootId, path.Canonical, headBranch, baseBranch)
                        : body;

                    var existing = await gh.ListPullRequestsAsync(
                        slug,
                        new PrListFilters(Head: headBranch, Base: baseBranch, State: "open", Limit: 1),
                        innerCt).ConfigureAwait(false);
                    if (existing.Count > 0)
                    {
                        var found = existing[0];
                        var result = new PrOpenMergeGroupResult
                        {
                            PrNumber = found.Number,
                            PrUrl = found.Url ?? "",
                            Title = prTitle,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RootId = rootId,
                            MgPath = path.Canonical,
                            Created = false,
                        };
                        payload = new PrOpenMergeGroupPrPayload
                        {
                            RootId = rootId,
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
                        EmitMergeGroup(result);
                        return ExitCodes.Success;
                    }

                    var url = await gh.CreatePullRequestAsync(slug, baseBranch, headBranch, prTitle, prBody, innerCt).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        const string error = "gh pr create failed — no URL returned";
                        payload = new PrOpenMergeGroupPrPayload
                        {
                            RootId = rootId,
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
                        EmitMgError(rootId, mgPath, error, headBranch: headBranch, baseBranch: baseBranch);
                        return ExitCodes.RoutingFailure;
                    }

                    var trimmedUrl = url.Trim();
                    var createdResult = new PrOpenMergeGroupResult
                    {
                        PrNumber = ExtractPrNumber(trimmedUrl),
                        PrUrl = trimmedUrl,
                        Title = prTitle,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RootId = rootId,
                        MgPath = path.Canonical,
                        Created = true,
                    };
                    payload = new PrOpenMergeGroupPrPayload
                    {
                        RootId = rootId,
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
                    EmitMergeGroup(createdResult);
                    return ExitCodes.Success;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    payload = new PrOpenMergeGroupPrPayload
                    {
                        RootId = rootId,
                        MergeGroupPath = path.Canonical,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        Error = ex.Message,
                    };
                    EmitMgError(rootId, mgPath, ex.Message, headBranch: headBranch, baseBranch: baseBranch);
                    return ExitCodes.RoutingFailure;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.PrOpenMergeGroupPrPayload),
            effectsSelector: _ => SelectOpenMergeGroupPrEffects(payload),
            ct: ct).ConfigureAwait(false);
    }

    private static string BuildDefaultMgBody(int rootId, string mgPath, string headBranch, string baseBranch)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## Merge group `").Append(mgPath).Append("` for root #").Append(rootId).Append("\n\n");
        sb.Append("Promotes `").Append(headBranch).Append("` into `").Append(baseBranch).Append("`.\n\n");
        sb.Append("This PR was opened by `polyphony pr open-mg-pr`. The detailed body — including the manifest of items in this merge group — is composed by the orchestrating workflow when it has that context.\n");
        return sb.ToString();
    }

    private static void EmitMergeGroup(PrOpenMergeGroupResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenMergeGroupResult));

    private static void EmitMgError(
        int rootId,
        string mgPath,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitMergeGroup(new PrOpenMergeGroupResult
        {
            PrNumber = 0,
            PrUrl = "",
            Title = "",
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            RootId = rootId,
            MgPath = mgPath,
            Created = false,
            Error = message,
        });
    }
}
