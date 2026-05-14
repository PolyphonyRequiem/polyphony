using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.AzureDevOps;
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
///   <item>Polyphony-authored ADO comments (archived to a sidecar JSON before
///         deletion).</item>
///   <item>Per-root state dir at <c>&lt;git-common-dir&gt;/polyphony/N/</c>.</item>
///   <item>Per-root branches (local + remote): <c>feature/N</c>, <c>plan/N*</c>,
///         <c>impl/N-*</c>, <c>mg/N_*</c>, <c>evidence/N-*</c>.</item>
///   <item>Linked worktrees whose checked-out branch belongs to this root.</item>
/// </list>
///
/// <para>UX: <c>--dry-run</c> enumerates artifacts without mutation.
/// <c>--force</c> skips the confirmation gate. Without either flag the verb
/// renders the plan to STDERR and prompts for confirmation on STDIN; in
/// non-interactive environments (redirected STDIN) it emits a
/// <c>needs_confirmation</c> envelope instead.</para>
/// </summary>
[VerbGroup("reset")]
public sealed class ResetCommands(
    ITwigClient twig,
    IWorkItemRepository repository,
    IGitClient git,
    HierarchyWalker walker,
    PolyphonyStatePaths statePaths,
    IWorkItemCommentClient commentClient)
{
    /// <summary>Archive subdirectory name under the git common dir.</summary>
    internal const string ArchiveSubdirName = "polyphony-archive";

    /// <summary>Test seam: override <see cref="Console.IsInputRedirected"/>.</summary>
    internal Func<bool> IsInputRedirected { get; init; } = () => Console.IsInputRedirected;

    /// <summary>Test seam: override <see cref="Console.ReadLine"/>.</summary>
    internal Func<string?> ReadLine { get; init; } = Console.ReadLine;

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

        // 2. Comments: collect from all items in scope.
        var allComments = await EnumerateCommentsAsync(patchedItemIds, ct).ConfigureAwait(false);
        int commentCount = allComments.Sum(c => c.Comments.Length);

        // 3. State dir.
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

        // 4. Branches (local + remote) belonging to this root.
        var (localBranches, remoteBranches) = await EnumerateBranchesAsync(rootId, ct).ConfigureAwait(false);

        // 5. Worktrees whose branch belongs to this root.
        var worktreePaths = await EnumerateWorktreesAsync(rootId, ct).ConfigureAwait(false);

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
                CommentsArchived = commentCount,
                LocalBranchesDeleted = localBranches,
                RemoteBranchesDeleted = remoteBranches,
                WorktreesRemoved = worktreePaths,
                StateDir = stateDir,
                StateDirDeleted = stateDirExists,
            });
            return ExitCodes.Success;
        }

        if (!force)
        {
            // Interactive mode: render plan to STDERR and prompt on STDIN.
            // In non-interactive environments (redirected STDIN), fall back
            // to the routing-style needs_confirmation envelope.
            if (IsInputRedirected())
            {
                Emit(new ResetResult
                {
                    RootId = rootId,
                    Action = "needs_confirmation",
                    DryRun = false,
                    TagsRemoved = allTagStrings,
                    ItemsPatched = patchedItemIds,
                    CommentsArchived = commentCount,
                    LocalBranchesDeleted = localBranches,
                    RemoteBranchesDeleted = remoteBranches,
                    WorktreesRemoved = worktreePaths,
                    StateDir = stateDir,
                    StateDirDeleted = stateDirExists,
                });
                return ExitCodes.Success;
            }

            // Bypass Program.cs stderr capture by writing directly to the
            // underlying stream so the user sees the plan before being prompted.
            using var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            RenderPlan(stderr, rootId, allTagStrings, patchedItemIds, commentCount,
                localBranches, remoteBranches, worktreePaths, stateDir, stateDirExists);
            stderr.Write("Continue? [y/N] ");
            var response = ReadLine();

            if (!string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Emit(new ResetResult
                {
                    RootId = rootId,
                    Action = "cancelled",
                    DryRun = false,
                });
                return ExitCodes.Success;
            }
        }

        // ── Execute phase ────────────────────────────────────────────────

        // 1. Archive comments to sidecar JSON BEFORE clearing.
        string? archivePath = null;
        if (commentCount > 0)
        {
            archivePath = await ArchiveCommentsAsync(rootId, allComments, ct).ConfigureAwait(false);
        }

        // 2. Clear comments from ADO.
        foreach (var item in allComments)
        {
            var org = await twig.GetConfigValueAsync("organization", ct).ConfigureAwait(false);
            var project = await twig.GetConfigValueAsync("project", ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(org) || string.IsNullOrEmpty(project))
                break;

            foreach (var comment in item.Comments)
            {
                await commentClient.DeleteCommentAsync(org, project, item.WorkItemId, comment.CommentId, ct)
                    .ConfigureAwait(false);
            }
        }

        // 3. Strip tags from all affected items.
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

        // 4. Delete state dir.
        bool stateDirDeleted = false;
        if (stateDir is not null && stateDirExists)
        {
            Directory.Delete(stateDir, recursive: true);
            stateDirDeleted = true;
        }

        // 5. Remove worktrees (before branch deletion — git refuses to
        //    delete a branch that is checked out in a worktree).
        var removedWorktrees = new List<string>();
        foreach (var wtPath in worktreePaths)
        {
            var result = await git.WorktreeRemoveAsync(wtPath, force: true, ct).ConfigureAwait(false);
            if (result.Succeeded)
                removedWorktrees.Add(wtPath);
        }

        // 6. Delete local branches.
        var deletedLocal = new List<string>();
        foreach (var branch in localBranches)
        {
            var result = await git.DeleteLocalBranchAsync(branch, ct).ConfigureAwait(false);
            if (result.Succeeded)
                deletedLocal.Add(branch);
        }

        // 7. Delete remote branches.
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
            CommentsArchived = commentCount,
            ArchivePath = archivePath,
            LocalBranchesDeleted = [.. deletedLocal],
            RemoteBranchesDeleted = [.. deletedRemote],
            WorktreesRemoved = [.. removedWorktrees],
            StateDir = stateDir,
            StateDirDeleted = stateDirDeleted,
        });
        return ExitCodes.Success;
    }

    // ── Comment enumeration + archiving ──────────────────────────────────

    private async Task<IReadOnlyList<ArchivedWorkItemComments>> EnumerateCommentsAsync(
        int[] itemIds,
        CancellationToken ct)
    {
        var org = await twig.GetConfigValueAsync("organization", ct).ConfigureAwait(false);
        var project = await twig.GetConfigValueAsync("project", ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(org) || string.IsNullOrEmpty(project))
            return [];

        var result = new List<ArchivedWorkItemComments>();
        foreach (var itemId in itemIds)
        {
            try
            {
                var comments = await commentClient.ListCommentsAsync(org, project, itemId, ct)
                    .ConfigureAwait(false);
                if (comments.Count > 0)
                {
                    result.Add(new ArchivedWorkItemComments
                    {
                        WorkItemId = itemId,
                        Comments = comments.Select(c => new ArchivedComment
                        {
                            CommentId = c.CommentId,
                            Text = c.Text,
                            CreatedBy = c.CreatedBy,
                            CreatedDate = c.CreatedDate,
                        }).ToArray(),
                    });
                }
            }
            catch (HttpRequestException)
            {
                // Comment enumeration is best-effort per item.
            }
        }

        return result;
    }

    private async Task<string> ArchiveCommentsAsync(
        int rootId,
        IReadOnlyList<ArchivedWorkItemComments> comments,
        CancellationToken ct)
    {
        var archive = new ResetCommentArchive
        {
            RootId = rootId,
            ArchivedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Items = [.. comments],
        };

        var archiveDir = await ResolveArchiveDirAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(archiveDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var archivePath = Path.Combine(archiveDir, $"{rootId}-{timestamp}.json");
        var json = JsonSerializer.Serialize(archive, PolyphonyJsonContext.Default.ResetCommentArchive);
        await File.WriteAllTextAsync(archivePath, json, ct).ConfigureAwait(false);

        return archivePath;
    }

    private async Task<string> ResolveArchiveDirAsync(CancellationToken ct)
    {
        try
        {
            var stateBase = await statePaths.GetStateBaseAsync(ct).ConfigureAwait(false);
            // Sibling to polyphony/ state dir: <git-common-dir>/polyphony-archive/
            var parent = Path.GetDirectoryName(stateBase) ?? stateBase;
            return Path.Combine(parent, ArchiveSubdirName);
        }
        catch (InvalidOperationException)
        {
            // Not in a git repo — fall back to cwd.
            return Path.Combine(Directory.GetCurrentDirectory(), ArchiveSubdirName);
        }
    }

    // ── Worktree enumeration ─────────────────────────────────────────────

    private async Task<string[]> EnumerateWorktreesAsync(int rootId, CancellationToken ct)
    {
        var result = await git.WorktreeListAsync(ct).ConfigureAwait(false);
        if (!result.Succeeded)
            return [];

        return ParseWorktreesForRoot(result.Stdout, rootId);
    }

    /// <summary>
    /// Parse <c>git worktree list --porcelain</c> output and return paths
    /// whose branch belongs to the given root.
    /// </summary>
    internal static string[] ParseWorktreesForRoot(string porcelainOutput, int rootId)
    {
        var matching = new List<string>();
        string? currentPath = null;
        var rid = RootId.Parse(rootId);

        foreach (var line in porcelainOutput.Split('\n', StringSplitOptions.None))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("worktree ", StringComparison.Ordinal))
            {
                currentPath = trimmed.Substring("worktree ".Length);
            }
            else if (trimmed.StartsWith("branch ", StringComparison.Ordinal) && currentPath is not null)
            {
                var refName = trimmed.Substring("branch ".Length);
                var branchName = refName.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? refName.Substring("refs/heads/".Length)
                    : refName;

                var parsed = BranchNameParser.ParseOrUnrecognized(branchName);
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
                    matching.Add(currentPath);
            }
            else if (trimmed.Length == 0)
            {
                currentPath = null;
            }
        }

        return [.. matching];
    }

    // ── Tag enumeration ──────────────────────────────────────────────────

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

    // ── Branch enumeration ───────────────────────────────────────────────

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

    // ── Helpers ──────────────────────────────────────────────────────────

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

    private static void RenderPlan(
        StreamWriter stderr, int rootId,
        string[] tags, int[] items, int commentCount,
        string[] localBranches, string[] remoteBranches,
        string[] worktrees, string? stateDir, bool stateDirExists)
    {
        stderr.WriteLine($"Reset plan for root {rootId}:");
        stderr.WriteLine($"  Tags to remove:      {tags.Length} across {items.Length} item(s)");
        stderr.WriteLine($"  Comments to archive:  {commentCount}");
        stderr.WriteLine($"  Local branches:       {localBranches.Length}");
        stderr.WriteLine($"  Remote branches:      {remoteBranches.Length}");
        stderr.WriteLine($"  Worktrees:            {worktrees.Length}");
        stderr.WriteLine($"  State dir:            {(stateDirExists ? stateDir : "(none)")}");
    }

    private sealed record TagRemoval(int ItemId, string[] Tags);
}
