using System.Globalization;
using System.Text.Json.Nodes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    private static IReadOnlyList<JournalResourceEffect> SelectCreateFeaturePrEffects(PrCreateFeaturePrPayload? payload)
        => SelectOpenPullRequestEffects(
            payload,
            ResourceKind.GitHubPr,
            "github",
            payload?.WorkItemId is > 0 ? WorkItemJournalTarget(payload.WorkItemId) : null,
            ("feature_branch", payload?.FeatureBranch),
            ("target_branch", payload?.TargetBranch));

    private static IReadOnlyList<JournalResourceEffect> SelectCreateFeatureAdoEffects(PrCreateFeatureAdoPayload? payload)
        => SelectOpenPullRequestEffects(
            payload,
            ResourceKind.AdoPr,
            "ado",
            payload?.RootId is > 0 ? WorkItemJournalTarget(payload.RootId) : null,
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("head_branch", payload?.HeadBranch),
            ("base_branch", payload?.BaseBranch));

    private static IReadOnlyList<JournalResourceEffect> SelectOpenPlanPrEffects(PrOpenPlanPrPayload? payload)
        => SelectOpenPullRequestEffects(
            payload,
            ResourceKind.GitHubPr,
            "github",
            payload?.ItemId is > 0 ? WorkItemJournalTarget(payload.ItemId) : null,
            ("root_id", payload?.RootId),
            ("item_id", payload?.ItemId),
            ("parent_item_id", payload?.ParentItemId),
            ("item_key", payload?.ItemKey),
            ("is_root_plan", payload?.IsRootPlan),
            ("head_branch", payload?.HeadBranch),
            ("base_branch", payload?.BaseBranch),
            ("stale", payload?.Stale));

    private static IReadOnlyList<JournalResourceEffect> SelectOpenPlanAdoEffects(PrOpenPlanAdoPayload? payload)
        => SelectOpenPullRequestEffects(
            payload,
            ResourceKind.AdoPr,
            "ado",
            payload?.ItemId is > 0 ? WorkItemJournalTarget(payload.ItemId) : null,
            ("root_id", payload?.RootId),
            ("item_id", payload?.ItemId),
            ("parent_item_id", payload?.ParentItemId),
            ("item_key", payload?.ItemKey),
            ("is_root_plan", payload?.IsRootPlan),
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("head_branch", payload?.HeadBranch),
            ("base_branch", payload?.BaseBranch),
            ("stale", payload?.Stale));

    private static IReadOnlyList<JournalResourceEffect> SelectOpenMergeGroupPrEffects(PrOpenMergeGroupPrPayload? payload)
        => SelectOpenPullRequestEffects(
            payload,
            ResourceKind.GitHubPr,
            "github",
            payload?.RootId is > 0 ? WorkItemJournalTarget(payload.RootId) : null,
            ("root_id", payload?.RootId),
            ("merge_group_path", payload?.MergeGroupPath),
            ("head_branch", payload?.HeadBranch),
            ("base_branch", payload?.BaseBranch));

    private static IReadOnlyList<JournalResourceEffect> SelectOpenMergeGroupAdoEffects(PrOpenMergeGroupAdoPayload? payload)
        => SelectOpenPullRequestEffects(
            payload,
            ResourceKind.AdoPr,
            "ado",
            payload?.RootId is > 0 ? WorkItemJournalTarget(payload.RootId) : null,
            ("root_id", payload?.RootId),
            ("merge_group_path", payload?.MergeGroupPath),
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("head_branch", payload?.HeadBranch),
            ("base_branch", payload?.BaseBranch));

    private static IReadOnlyList<JournalResourceEffect> SelectOpenImplPrEffects(PrOpenImplPrPayload? payload)
        => SelectOpenPullRequestEffects(
            payload,
            ResourceKind.GitHubPr,
            "github",
            payload?.ItemId is > 0 ? WorkItemJournalTarget(payload.ItemId) : null,
            ("root_id", payload?.RootId),
            ("item_id", payload?.ItemId),
            ("merge_group_path", payload?.MergeGroupPath),
            ("head_branch", payload?.HeadBranch),
            ("base_branch", payload?.BaseBranch));

    private static IReadOnlyList<JournalResourceEffect> SelectOpenImplAdoEffects(PrOpenImplAdoPayload? payload)
        => SelectOpenPullRequestEffects(
            payload,
            ResourceKind.AdoPr,
            "ado",
            payload?.ItemId is > 0 ? WorkItemJournalTarget(payload.ItemId) : null,
            ("root_id", payload?.RootId),
            ("item_id", payload?.ItemId),
            ("merge_group_path", payload?.MergeGroupPath),
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("head_branch", payload?.HeadBranch),
            ("base_branch", payload?.BaseBranch));

    private static IReadOnlyList<JournalResourceEffect> SelectOpenEvidencePrEffects(PrOpenEvidencePrPayload? payload)
        => SelectOpenPullRequestEffects(
            payload,
            ResourceKind.GitHubPr,
            "github",
            payload?.WorkItemId is > 0 ? WorkItemJournalTarget(payload.WorkItemId) : null,
            ("root_id", payload?.RootId),
            ("work_item_id", payload?.WorkItemId),
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("head_branch", payload?.HeadBranch),
            ("base_branch", payload?.BaseBranch));

    private static IReadOnlyList<JournalResourceEffect> SelectOpenEvidenceAdoEffects(PrOpenEvidenceAdoPayload? payload)
        => SelectOpenPullRequestEffects(
            payload,
            ResourceKind.AdoPr,
            "ado",
            payload?.WorkItemId is > 0 ? WorkItemJournalTarget(payload.WorkItemId) : null,
            ("root_id", payload?.RootId),
            ("work_item_id", payload?.WorkItemId),
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("head_branch", payload?.HeadBranch),
            ("base_branch", payload?.BaseBranch));

    private static IReadOnlyList<JournalResourceEffect> SelectMergePlanPrEffects(PrMergePlanPrPayload? payload)
        => SelectMergePullRequestEffects(
            payload,
            ResourceKind.GitHubPr,
            "github",
            payload?.ItemId is > 0 ? WorkItemJournalTarget(payload.ItemId) : null,
            payload?.HeadBranch,
            payload?.BaseBranch,
            deleteBranch: false,
            ("root_id", payload?.RootId),
            ("item_id", payload?.ItemId),
            ("parent_item_id", payload?.ParentItemId),
            ("item_key", payload?.ItemKey),
            ("manifest_branch", payload?.ManifestBranch),
            ("lock_token", payload?.LockToken),
            ("manifest_recorded", payload?.ManifestRecorded),
            ("manifest_pushed", payload?.ManifestPushed));

    private static IReadOnlyList<JournalResourceEffect> SelectMergePlanAdoEffects(PrMergePlanAdoPayload? payload)
        => SelectMergePullRequestEffects(
            payload,
            ResourceKind.AdoPr,
            "ado",
            payload?.ItemId is > 0 ? WorkItemJournalTarget(payload.ItemId) : null,
            payload?.HeadBranch,
            payload?.BaseBranch,
            deleteBranch: false,
            ("root_id", payload?.RootId),
            ("item_id", payload?.ItemId),
            ("parent_item_id", payload?.ParentItemId),
            ("item_key", payload?.ItemKey),
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("manifest_branch", payload?.ManifestBranch),
            ("lock_token", payload?.LockToken),
            ("manifest_recorded", payload?.ManifestRecorded),
            ("manifest_pushed", payload?.ManifestPushed));

    private static IReadOnlyList<JournalResourceEffect> SelectMergeMergeGroupPrEffects(PrMergeMergeGroupPrPayload? payload)
        => SelectMergePullRequestEffects(
            payload,
            ResourceKind.GitHubPr,
            "github",
            payload?.RootId is > 0 ? WorkItemJournalTarget(payload.RootId) : null,
            payload?.HeadBranch,
            payload?.BaseBranch,
            payload?.DeleteBranch ?? false,
            ("root_id", payload?.RootId),
            ("merge_group_path", payload?.MergeGroupPath),
            ("method", payload?.Method));

    private static IReadOnlyList<JournalResourceEffect> SelectMergeMergeGroupAdoEffects(PrMergeMergeGroupAdoPayload? payload)
        => SelectMergePullRequestEffects(
            payload,
            ResourceKind.AdoPr,
            "ado",
            payload?.RootId is > 0 ? WorkItemJournalTarget(payload.RootId) : null,
            payload?.HeadBranch,
            payload?.BaseBranch,
            payload?.DeleteBranch ?? false,
            ("root_id", payload?.RootId),
            ("merge_group_path", payload?.MergeGroupPath),
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("method", payload?.Method));

    private static IReadOnlyList<JournalResourceEffect> SelectMergeImplPrEffects(PrMergeImplPrPayload? payload)
        => SelectMergePullRequestEffects(
            payload,
            ResourceKind.GitHubPr,
            "github",
            payload?.ItemId is > 0 ? WorkItemJournalTarget(payload.ItemId) : null,
            payload?.HeadBranch,
            payload?.BaseBranch,
            payload?.DeleteBranch ?? false,
            ("root_id", payload?.RootId),
            ("item_id", payload?.ItemId),
            ("merge_group_path", payload?.MergeGroupPath),
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("method", payload?.Method));

    private static IReadOnlyList<JournalResourceEffect> SelectMergeImplAdoEffects(PrMergeImplAdoPayload? payload)
        => SelectMergePullRequestEffects(
            payload,
            ResourceKind.AdoPr,
            "ado",
            payload?.ItemId is > 0 ? WorkItemJournalTarget(payload.ItemId) : null,
            payload?.HeadBranch,
            payload?.BaseBranch,
            payload?.DeleteBranch ?? false,
            ("root_id", payload?.RootId),
            ("item_id", payload?.ItemId),
            ("merge_group_path", payload?.MergeGroupPath),
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("method", payload?.Method));

    private static IReadOnlyList<JournalResourceEffect> SelectMergeEvidencePrEffects(PrMergeEvidencePrPayload? payload)
        => SelectMergePullRequestEffects(
            payload,
            payload?.Organization is { Length: > 0 } ? ResourceKind.AdoPr : ResourceKind.GitHubPr,
            payload?.Organization is { Length: > 0 } ? "ado" : "github",
            null,
            headBranch: null,
            baseBranch: null,
            deleteBranch: false,
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository));

    private static IReadOnlyList<JournalResourceEffect> SelectMergeEvidenceAdoEffects(PrMergeEvidenceAdoPayload? payload)
        => SelectMergePullRequestEffects(
            payload,
            ResourceKind.AdoPr,
            "ado",
            null,
            headBranch: null,
            baseBranch: null,
            deleteBranch: false,
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository));

    private static IReadOnlyList<JournalResourceEffect> SelectMergeFeatureAdoEffects(PrMergeFeatureAdoPayload? payload)
        => SelectMergePullRequestEffects(
            payload,
            ResourceKind.AdoPr,
            "ado",
            payload?.RootId is > 0 ? WorkItemJournalTarget(payload.RootId) : null,
            payload?.HeadBranch,
            payload?.BaseBranch,
            payload?.DeleteBranch ?? false,
            ("root_id", payload?.RootId),
            ("organization", payload?.Organization),
            ("project", payload?.Project),
            ("repository", payload?.Repository),
            ("method", payload?.Method));

    private static IReadOnlyList<JournalResourceEffect> SelectPostCommentAdoEffects(PrPostCommentAdoPayload? payload)
    {
        if (payload is null || !payload.Succeeded || payload.CommentId is not { } commentId)
        {
            return [];
        }

        return
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.AdoPrComment,
                Id = commentId.ToString(CultureInfo.InvariantCulture),
                Intent = ResourceIntent.EnsurePresent,
                Mutation = ResourceMutation.CreatedNow,
                PolyphonyOwned = true,
                Platform = "ado",
                ParentId = PullRequestJournalTarget(payload.PrUrl ?? string.Empty, payload.PrNumber),
                Attributes = CreateAttributes(
                    ("organization", payload.Organization),
                    ("project", payload.Project),
                    ("repository", payload.Repository),
                    ("repo_slug", payload.RepoSlug),
                    ("pr_number", payload.PrNumber),
                    ("pr_url", payload.PrUrl),
                    ("thread_id", payload.ThreadId),
                    ("body", payload.Body)),
            },
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectVoteAdoEffects(PrVoteAdoPayload? payload)
    {
        if (payload is null || !payload.Succeeded || string.IsNullOrWhiteSpace(payload.ReviewerId))
        {
            return [];
        }

        return
        [
            new JournalResourceEffect
            {
                Kind = ResourceKind.AdoPrVote,
                Id = payload.ReviewerId,
                Intent = ResourceIntent.SetState,
                Mutation = payload.WasMutated ? ResourceMutation.Changed : ResourceMutation.NoChangedAlreadySatisfied,
                PolyphonyOwned = true,
                Platform = "ado",
                ParentId = PullRequestJournalTarget(payload.PrUrl ?? string.Empty, payload.PrNumber),
                Attributes = CreateAttributes(
                    ("organization", payload.Organization),
                    ("project", payload.Project),
                    ("repository", payload.Repository),
                    ("repo_slug", payload.RepoSlug),
                    ("pr_number", payload.PrNumber),
                    ("pr_url", payload.PrUrl),
                    ("vote", payload.Vote),
                    ("vote_value", payload.VoteValue)),
            },
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectOpenPullRequestEffects<TPayload>(
        TPayload? payload,
        string kind,
        string platform,
        string? parentId,
        params (string Name, object? Value)[] extraAttributes)
        where TPayload : class
    {
        if (!TryReadPrEffectInputs(payload, out var prId, out var prNumber, out var prUrl, out var repoSlug, out var title, out _, out var wasMutated, out _, out _))
        {
            return [];
        }

        var attributes = CreateAttributes(
            ("pr_number", prNumber),
            ("pr_url", prUrl),
            ("repo_slug", repoSlug),
            ("title", title));
        AppendAttributes(attributes, extraAttributes);

        return
        [
            new JournalResourceEffect
            {
                Kind = kind,
                Id = prId,
                Intent = ResourceIntent.EnsurePresent,
                Mutation = wasMutated ? ResourceMutation.CreatedNow : ResourceMutation.NoChangedExternalAlreadyPresent,
                PolyphonyOwned = wasMutated,
                Platform = platform,
                ParentId = parentId,
                Attributes = attributes,
            },
        ];
    }

    private static IReadOnlyList<JournalResourceEffect> SelectMergePullRequestEffects<TPayload>(
        TPayload? payload,
        string kind,
        string platform,
        string? parentId,
        string? headBranch,
        string? baseBranch,
        bool deleteBranch,
        params (string Name, object? Value)[] extraAttributes)
        where TPayload : class
    {
        if (!TryReadPrEffectInputs(payload, out var prId, out var prNumber, out var prUrl, out var repoSlug, out _, out _, out var wasMutated, out var mergeCommit, out var alreadyMerged))
        {
            return [];
        }

        var prAttributes = CreateAttributes(
            ("pr_number", prNumber),
            ("pr_url", prUrl),
            ("repo_slug", repoSlug),
            ("state", "merged"),
            ("merged_sha", mergeCommit),
            ("already_merged", alreadyMerged),
            ("head_branch", headBranch),
            ("base_branch", baseBranch));
        AppendAttributes(prAttributes, extraAttributes);

        var effects = new List<JournalResourceEffect>
        {
            new()
            {
                Kind = kind,
                Id = prId,
                Intent = ResourceIntent.SetState,
                Mutation = wasMutated ? ResourceMutation.Changed : ResourceMutation.NoChangedAlreadySatisfied,
                PolyphonyOwned = wasMutated,
                Platform = platform,
                ParentId = parentId,
                Attributes = prAttributes,
            },
        };

        if (!string.IsNullOrWhiteSpace(baseBranch))
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = baseBranch,
                Intent = ResourceIntent.AdvancePointer,
                Mutation = wasMutated ? ResourceMutation.Changed : ResourceMutation.NoChangedAlreadySatisfied,
                PolyphonyOwned = false,
                Attributes = CreateAttributes(
                    ("new_sha", mergeCommit),
                    ("via_pr", prId)),
            });
        }

        if (deleteBranch && !string.IsNullOrWhiteSpace(headBranch))
        {
            effects.Add(new JournalResourceEffect
            {
                Kind = ResourceKind.GitBranch,
                Id = headBranch,
                Intent = ResourceIntent.EnsureAbsent,
                Mutation = wasMutated ? ResourceMutation.DeletedNow : ResourceMutation.NoChangedAlreadySatisfied,
                PolyphonyOwned = false,
                Attributes = CreateAttributes(
                    ("via_pr", prId)),
            });
        }

        return effects;
    }

    private static bool TryReadPrEffectInputs<TPayload>(
        TPayload? payload,
        out string prId,
        out int prNumber,
        out string? prUrl,
        out string? repoSlug,
        out string? title,
        out bool succeeded,
        out bool wasMutated,
        out string? mergeCommit,
        out bool alreadyMerged)
        where TPayload : class
    {
        prId = string.Empty;
        prNumber = 0;
        prUrl = null;
        repoSlug = null;
        title = null;
        succeeded = false;
        wasMutated = false;
        mergeCommit = null;
        alreadyMerged = false;

        switch (payload)
        {
            case PrCreateFeaturePrPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                title = p.Title;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                break;
            case PrCreateFeatureAdoPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                title = p.Title;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                break;
            case PrOpenPlanPrPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                title = p.Title;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                break;
            case PrOpenPlanAdoPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                title = p.Title;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                break;
            case PrOpenMergeGroupPrPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                title = p.Title;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                break;
            case PrOpenMergeGroupAdoPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                title = p.Title;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                break;
            case PrOpenImplPrPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                title = p.Title;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                break;
            case PrOpenImplAdoPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                title = p.Title;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                break;
            case PrOpenEvidencePrPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                title = p.Title;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                break;
            case PrOpenEvidenceAdoPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                title = p.Title;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                break;
            case PrMergePlanPrPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                mergeCommit = p.MergeCommit;
                alreadyMerged = p.AlreadyMerged;
                break;
            case PrMergePlanAdoPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                mergeCommit = p.MergeCommit;
                alreadyMerged = p.AlreadyMerged;
                break;
            case PrMergeMergeGroupPrPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                mergeCommit = p.MergeCommit;
                alreadyMerged = p.AlreadyMerged;
                break;
            case PrMergeMergeGroupAdoPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                mergeCommit = p.MergeCommit;
                alreadyMerged = p.AlreadyMerged;
                break;
            case PrMergeImplPrPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                mergeCommit = p.MergeCommit;
                alreadyMerged = p.AlreadyMerged;
                break;
            case PrMergeImplAdoPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                mergeCommit = p.MergeCommit;
                alreadyMerged = p.AlreadyMerged;
                break;
            case PrMergeEvidencePrPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                mergeCommit = p.MergeCommit;
                alreadyMerged = p.AlreadyMerged;
                break;
            case PrMergeEvidenceAdoPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                mergeCommit = p.MergeCommit;
                alreadyMerged = p.AlreadyMerged;
                break;
            case PrMergeFeatureAdoPayload p:
                prNumber = p.PrNumber;
                prUrl = p.PrUrl;
                repoSlug = p.RepoSlug;
                succeeded = p.Succeeded;
                wasMutated = p.WasMutated;
                mergeCommit = p.MergeCommit;
                alreadyMerged = p.AlreadyMerged;
                break;
            default:
                return false;
        }

        if (!succeeded)
        {
            return false;
        }

        prId = PullRequestJournalTarget(prUrl ?? string.Empty, prNumber);
        return !string.IsNullOrWhiteSpace(prId);
    }

    private static string WorkItemJournalTarget(int workItemId) => $"workitem:{workItemId}";

    private static JsonObject CreateAttributes(params (string Name, object? Value)[] attributes)
    {
        var result = new JsonObject();
        AppendAttributes(result, attributes);
        return result;
    }

    private static void AppendAttributes(JsonObject attributes, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            attributes[name] = JsonValue.Create(value);
        }
    }
}
