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
    /// Merge a merge-group PR into its parent. Identifies the PR by its
    /// (head, base) pair: head is <c>mg/{root_id}_{mg_path}</c>; base is
    /// the parent merge-group branch when nested, or the feature branch
    /// when top-level. The merge method is hardcoded to <c>merge-commit</c>
    /// per ADR <c>docs/decisions/branch-model.md</c> — nested merge groups
    /// depend on git ancestry to know what is integrated; squash and rebase
    /// would break the chain. The head branch is never deleted (sibling
    /// merge groups may still be in flight).
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's root (focus) item.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path being merged.</param>
    /// <param name="admin">Pass <c>--admin</c> to bypass branch-protection requirements.</param>
    /// <param name="matchHeadCommit">
    /// When set, gh refuses to merge if the MG branch SHA has moved off
    /// this commit. Use to guard against races between status checks and
    /// merge.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("merge-mg-pr")]
    [JournaledAction(Action = "pr_merge_mg_pr")]
    [VerbResult(typeof(PrMergeMergeGroupResult))]
    public async Task<int> MergeMergeGroupPr(
        int rootId = RequiredInput.MissingInt,
        string mgPath = "",
        bool admin = false,
        string matchHeadCommit = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr merge-mg-pr",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        const string MgMethod = "merge";
        const bool MgDeleteBranch = false;

        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitMergeMgError(rootId, mgPath, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }

        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitMergeMgError(
                rootId,
                mgPath,
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.ConfigError;
        }

        var headBranch = BranchNameBuilder.MergeGroup(root, path).Value;
        var baseBranch = path.IsTopLevel
            ? BranchNameBuilder.Feature(root).Value
            : BranchNameBuilder.MergeGroup(root, MergeGroupPath.Of(path.Segments.Take(path.Depth - 1))).Value;

        PrMergeMergeGroupPrPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("pr_merge_mg_pr", BranchPairJournalTarget(headBranch, baseBranch), rootId, rootId),
            async innerCt =>
            {
                try
                {
                    var slug = await TryResolveSlugAsync(innerCt).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(slug))
                    {
                        payload = new PrMergeMergeGroupPrPayload
                        {
                            RootId = rootId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            Method = MgMethod,
                            DeleteBranch = MgDeleteBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            AlreadyMerged = false,
                            Error = "Could not resolve repo slug from origin remote",
                        };
                        EmitMergeMgError(rootId, mgPath, "Could not resolve repo slug from origin remote", headBranch, baseBranch);
                        return ExitCodes.RoutingFailure;
                    }

                    var resolution = await FindPrForMergeAsync(slug, headBranch, baseBranch, innerCt).ConfigureAwait(false);
                    if (resolution.Error is not null)
                    {
                        payload = new PrMergeMergeGroupPrPayload
                        {
                            RootId = rootId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RepoSlug = slug,
                            Method = MgMethod,
                            DeleteBranch = MgDeleteBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            AlreadyMerged = false,
                            Error = resolution.Error,
                        };
                        EmitMergeMgError(rootId, mgPath, resolution.Error, headBranch, baseBranch);
                        return ExitCodes.RoutingFailure;
                    }

                    if (resolution.AlreadyMergedPr is { } already)
                    {
                        payload = new PrMergeMergeGroupPrPayload
                        {
                            RootId = rootId,
                            MergeGroupPath = path.Canonical,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RepoSlug = slug,
                            PrNumber = already.Number,
                            Method = MgMethod,
                            DeleteBranch = MgDeleteBranch,
                            ResultAction = "already_merged",
                            Succeeded = true,
                            WasMutated = false,
                            AlreadyMerged = true,
                        };
                        EmitMergeMg(new PrMergeMergeGroupResult
                        {
                            PrNumber = already.Number,
                            HeadBranch = headBranch,
                            BaseBranch = baseBranch,
                            RootId = rootId,
                            MgPath = path.Canonical,
                            Method = MgMethod,
                            Merged = true,
                            AlreadyMerged = true,
                            DeleteBranch = MgDeleteBranch,
                            MergeSha = null,
                        });
                        return ExitCodes.Success;
                    }

                    var openPr = resolution.OpenPr!;
                    var mergeMatch = string.IsNullOrEmpty(matchHeadCommit) ? null : matchHeadCommit;
                    var result = await gh.MergePullRequestAsync(
                        slug, openPr.Number, GhMergeMethod.Merge, admin, MgDeleteBranch, mergeMatch, ct: innerCt).ConfigureAwait(false);

                    payload = new PrMergeMergeGroupPrPayload
                    {
                        RootId = rootId,
                        MergeGroupPath = path.Canonical,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RepoSlug = slug,
                        PrNumber = openPr.Number,
                        Method = MgMethod,
                        DeleteBranch = MgDeleteBranch,
                        MergeCommit = result.MergeSha,
                        ResultAction = result.AlreadyMerged ? "already_merged" : (result.Succeeded ? "merged" : "error"),
                        Succeeded = result.Succeeded,
                        WasMutated = result.Succeeded && !result.AlreadyMerged,
                        AlreadyMerged = result.AlreadyMerged,
                        Error = result.Succeeded ? null : "gh merge returned not-succeeded",
                    };
                    EmitMergeMg(new PrMergeMergeGroupResult
                    {
                        PrNumber = openPr.Number,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        RootId = rootId,
                        MgPath = path.Canonical,
                        Method = MgMethod,
                        Merged = result.Succeeded,
                        AlreadyMerged = result.AlreadyMerged,
                        DeleteBranch = MgDeleteBranch,
                        MergeSha = result.MergeSha,
                    });
                    return result.Succeeded ? ExitCodes.Success : ExitCodes.RoutingFailure;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    payload = new PrMergeMergeGroupPrPayload
                    {
                        RootId = rootId,
                        MergeGroupPath = path.Canonical,
                        HeadBranch = headBranch,
                        BaseBranch = baseBranch,
                        Method = MgMethod,
                        DeleteBranch = MgDeleteBranch,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        AlreadyMerged = false,
                        Error = ex.Message,
                    };
                    EmitMergeMgError(rootId, mgPath, ex.Message, headBranch, baseBranch);
                    return ExitCodes.RoutingFailure;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.PrMergeMergeGroupPrPayload),
            ct: ct).ConfigureAwait(false);
    }

    private static void EmitMergeMg(PrMergeMergeGroupResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrMergeMergeGroupResult));

    private static void EmitMergeMgError(
        int rootId,
        string mgPath,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitMergeMg(new PrMergeMergeGroupResult
        {
            PrNumber = 0,
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            RootId = rootId,
            MgPath = mgPath,
            Method = "merge",
            Merged = false,
            AlreadyMerged = false,
            DeleteBranch = false,
            MergeSha = null,
            Error = message,
        });
    }
}
