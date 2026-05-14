using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Paths;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tagging;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony reset</c> — scrubs all polyphony-authored state for a single
/// root so the operator can re-dispatch cleanly.
///
/// <list type="bullet">
///   <item>ADO tags matching the <see cref="PolyphonyTag"/> DU (stripped from
///         the root and every in-scope descendant).</item>
///   <item>Per-root state dir at <c>&lt;git-common-dir&gt;/polyphony/N/</c>.</item>
///   <item>Per-root branches (local + remote): <c>feature/N</c>, <c>plan/N*</c>,
///         <c>impl/N-*</c>, <c>mg/N_*</c>, <c>evidence/N-*</c>.</item>
/// </list>
///
/// <para>UX: <c>--dry-run</c> enumerates artifacts without mutation.
/// <c>--force</c> skips the confirmation gate. Without either flag, the verb
/// emits a <c>needs_confirmation</c> envelope for the workflow to route.</para>
/// </summary>
[VerbGroup("reset")]
public sealed class ResetCommands(
    ITwigClient twig,
    IWorkItemRepository repository,
    IGitClient git,
    HierarchyWalker walker,
    PolyphonyStatePaths statePaths)
{
    /// <summary>
    /// Scrubs all polyphony-authored state for the given root.
    /// </summary>
    /// <param name="rootId">ADO work item ID of the root to reset.</param>
    /// <param name="dryRun">Enumerate artifacts without performing any mutation.</param>
    /// <param name="force">Skip the confirmation gate and execute immediately.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("run")]
    [VerbResult(typeof(ResetResult))]
    public async Task<int> Run(
        int rootId = RequiredInput.MissingInt,
        bool dryRun = false,
        bool force = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reset run",
            ("--root-id", rootId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        // Sync to get fresh tag state.
        await twig.SyncAsync(ct).ConfigureAwait(false);

        var rootItem = await repository.GetByIdAsync(rootId, ct).ConfigureAwait(false);
        if (rootItem is null)
        {
            Console.WriteLine($$"""{"error":"Work item {{rootId}} not found","work_item_id":{{rootId}}}""");
            return ExitCodes.CacheError;
        }

        // ── Plan phase: enumerate artifacts ──────────────────────────────

        // 1. Tags: walk subtree and collect polyphony-owned tags per item.
        var tagRemovals = await EnumerateTagRemovalsAsync(rootId, ct).ConfigureAwait(false);
        var allTagStrings = tagRemovals.SelectMany(r => r.Tags).Distinct().ToArray();
        var patchedItemIds = tagRemovals.Select(r => r.ItemId).ToArray();

        // 2. State dir.
        string? stateDir = null;
        bool stateDirExists = false;
        try
        {
            stateDir = await statePaths.GetStateRootAsync(rootId, ct).ConfigureAwait(false);
            stateDirExists = Directory.Exists(stateDir);
        }
        catch (InvalidOperationException)
        {
            // Not in a git repo — state dir resolution is best-effort.
        }

        // 3. Branches (local + remote) belonging to this root.
        var (localBranches, remoteBranches) = await EnumerateBranchesAsync(rootId, ct).ConfigureAwait(false);

        // ── Routing ──────────────────────────────────────────────────────

        if (dryRun)
        {
            Emit(new ResetResult
            {
                RootId = rootId,
                Action = "planned",
                DryRun = true,
                TagsRemoved = allTagStrings,
                ItemsPatched = patchedItemIds,
                LocalBranchesDeleted = localBranches,
                RemoteBranchesDeleted = remoteBranches,
                StateDir = stateDir,
                StateDirDeleted = stateDirExists,
            });
            return ExitCodes.Success;
        }

        if (!force)
        {
            Emit(new ResetResult
            {
                RootId = rootId,
                Action = "needs_confirmation",
                DryRun = false,
                TagsRemoved = allTagStrings,
                ItemsPatched = patchedItemIds,
                LocalBranchesDeleted = localBranches,
                RemoteBranchesDeleted = remoteBranches,
                StateDir = stateDir,
                StateDirDeleted = stateDirExists,
            });
            return ExitCodes.Success;
        }

        // ── Execute phase ────────────────────────────────────────────────

        // 1. Strip tags from all affected items.
        foreach (var removal in tagRemovals)
        {
            var item = await repository.GetByIdAsync(removal.ItemId, ct).ConfigureAwait(false);
            if (item is null) continue;

            item.Fields.TryGetValue("System.Tags", out var raw);
            var tagSet = TagSet.Parse(raw);
            var updated = tagSet;

            foreach (var tag in removal.Tags)
                updated = updated.Remove(tag);

            if (!ReferenceEquals(tagSet, updated))
            {
                await twig.PatchFieldsAsync(
                    removal.ItemId,
                    new Dictionary<string, string> { ["System.Tags"] = updated.Format() },
                    ct).ConfigureAwait(false);
            }
        }

        if (tagRemovals.Count > 0)
            await twig.SyncAsync(ct).ConfigureAwait(false);

        // 2. Delete state dir.
        bool stateDirDeleted = false;
        if (stateDir is not null && stateDirExists)
        {
            Directory.Delete(stateDir, recursive: true);
            stateDirDeleted = true;
        }

        // 3. Delete local branches.
        var deletedLocal = new List<string>();
        foreach (var branch in localBranches)
        {
            var result = await git.DeleteLocalBranchAsync(branch, ct).ConfigureAwait(false);
            if (result.Succeeded)
                deletedLocal.Add(branch);
        }

        // 4. Delete remote branches.
        var deletedRemote = new List<string>();
        foreach (var branch in remoteBranches)
        {
            var result = await git.DeleteRemoteBranchAsync("origin", branch, ct).ConfigureAwait(false);
            if (result.Succeeded)
                deletedRemote.Add(branch);
        }

        Emit(new ResetResult
        {
            RootId = rootId,
            Action = "executed",
            DryRun = false,
            TagsRemoved = allTagStrings,
            ItemsPatched = patchedItemIds,
            LocalBranchesDeleted = [.. deletedLocal],
            RemoteBranchesDeleted = [.. deletedRemote],
            StateDir = stateDir,
            StateDirDeleted = stateDirDeleted,
        });
        return ExitCodes.Success;
    }

    private async Task<IReadOnlyList<TagRemoval>> EnumerateTagRemovalsAsync(int rootId, CancellationToken ct)
    {
        var removals = new List<TagRemoval>();

        var hierarchy = await walker.WalkAsync(rootId, maxDepth: 10, ct).ConfigureAwait(false);
        if (hierarchy is null)
        {
            // Root itself may still have tags even if hierarchy walk fails.
            var rootItem = await repository.GetByIdAsync(rootId, ct).ConfigureAwait(false);
            if (rootItem is not null)
            {
                rootItem.Fields.TryGetValue("System.Tags", out var raw);
                var tags = CollectOwnedTags(raw);
                if (tags.Length > 0)
                    removals.Add(new TagRemoval(rootId, tags));
            }
            return removals;
        }

        foreach (var node in Flatten(hierarchy))
        {
            var ownedTags = CollectOwnedTags(node.Tags);
            if (ownedTags.Length > 0)
                removals.Add(new TagRemoval(node.WorkItemId, ownedTags));
        }

        return removals;
    }

    private static string[] CollectOwnedTags(string? rawTags)
    {
        var tagSet = TagSet.Parse(rawTags);
        return tagSet.Where(PolyphonyTag.IsPolyphonyOwned).ToArray();
    }

    private async Task<(string[] Local, string[] Remote)> EnumerateBranchesAsync(int rootId, CancellationToken ct)
    {
        var localBranches = await git.ListLocalBranchesAsync(ct).ConfigureAwait(false);
        var remoteBranches = await git.ListRemoteBranchesAsync(ct).ConfigureAwait(false);

        var matchingLocal = FilterBranchesByRoot(localBranches, rootId);
        var matchingRemote = FilterBranchesByRoot(remoteBranches, rootId);

        return (matchingLocal, matchingRemote);
    }

    private static string[] FilterBranchesByRoot(IReadOnlyList<string> branches, int rootId)
    {
        var matching = new List<string>();
        var rid = RootId.Parse(rootId);

        foreach (var branch in branches)
        {
            var parsed = BranchNameParser.ParseOrUnrecognized(branch);
            var belongs = parsed switch
            {
                ParsedBranch.Feature f => f.RootId == rid,
                ParsedBranch.RootPlan p => p.RootId == rid,
                ParsedBranch.DescendantPlan p => p.RootId == rid,
                ParsedBranch.MergeGroup mg => mg.RootId == rid,
                ParsedBranch.Impl i => i.RootId == rid,
                ParsedBranch.Evidence e => e.RootId == rid,
                ParsedBranch.EvidenceOrphan => false,
                ParsedBranch.Unrecognized => false,
                _ => false
            };

            if (belongs)
                matching.Add(branch);
        }

        return [.. matching];
    }

    private static IEnumerable<HierarchyResult> Flatten(HierarchyResult node)
    {
        yield return node;
        if (node.Children is null) yield break;
        foreach (var child in node.Children)
        {
            foreach (var sub in Flatten(child))
                yield return sub;
        }
    }

    private static void Emit(ResetResult r) =>
        Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ResetResult));

    private sealed record TagRemoval(int ItemId, string[] Tags);
}
