using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tagging;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Scope verbs (<c>polyphony scope ...</c>). Manage the bare <c>polyphony</c>
/// in-scope marker on work items. Authoritative spec: <c>docs/polyphony-tags.md</c>.
///
/// <list type="bullet">
///   <item><c>check</c> — read scope disposition for one item.</item>
///   <item><c>list</c> — enumerate in-scope vs out-of-scope items under a root.</item>
///   <item><c>tag</c> / <c>untag</c> — idempotent mutations of the bare <c>polyphony</c> tag.</item>
/// </list>
///
/// All verbs emit JSON to stdout. Routing-style success path always exits 0;
/// caller routes on the JSON. Hard errors (work item not found, twig sync
/// failures) emit JSON with <c>error</c> set and exit non-zero.
/// </summary>
[VerbGroup("scope")]
public sealed class ScopeCommands(
    ITwigClient twig,
    IWorkItemRepository repository,
    HierarchyWalker walker)
{
    /// <summary>
    /// Reads the scope disposition for one work item. Returns
    /// <c>{ work_item_id, in_scope, is_root, tags[] }</c>.
    /// </summary>
    /// <param name="workItem">ADO work item ID to inspect.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("check")]
    [VerbResult(typeof(ScopeCheckResult))]
    public async Task<int> Check(int workItem, CancellationToken ct = default)
    {
        await twig.SyncAsync(ct).ConfigureAwait(false);

        var item = await repository.GetByIdAsync(workItem, ct).ConfigureAwait(false);
        if (item is null)
        {
            EmitCheck(new ScopeCheckResult
            {
                WorkItemId = workItem,
                Tags = [],
                Error = $"Work item {workItem} not found",
            });
            return ExitCodes.RoutingFailure;
        }

        item.Fields.TryGetValue("System.Tags", out var raw);
        var tags = TagSet.Parse(raw);

        EmitCheck(new ScopeCheckResult
        {
            WorkItemId = workItem,
            InScope = PolyphonyTags.IsInScope(tags),
            IsRoot = PolyphonyTags.IsRoot(tags),
            Tags = tags.ToArray(),
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Walks the work hierarchy under <paramref name="rootId"/> and partitions
    /// items into in-scope (root + tagged descendants) vs out-of-scope.
    /// </summary>
    /// <param name="rootId">The root work item ID.</param>
    /// <param name="maxDepth">Max walk depth (default 5).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("list")]
    [VerbResult(typeof(ScopeListResult))]
    public async Task<int> List(int rootId, int maxDepth = 5, CancellationToken ct = default)
    {
        await twig.SyncAsync(ct).ConfigureAwait(false);

        var hierarchy = await walker.WalkAsync(rootId, maxDepth, ct).ConfigureAwait(false);
        if (hierarchy is null)
        {
            EmitList(new ScopeListResult
            {
                RootId = rootId,
                InScopeItems = [],
                OutOfScopeItems = [],
                Error = $"Root work item {rootId} not found",
            });
            return ExitCodes.RoutingFailure;
        }

        var inScope = new List<ScopeListItem>();
        var outOfScope = new List<ScopeListItem>();

        foreach (var node in Flatten(hierarchy))
        {
            var tags = TagSet.Parse(node.Tags);
            var isRoot = node.WorkItemId == rootId || PolyphonyTags.IsRoot(tags);
            // A root is implicitly in-scope. A descendant must carry the bare
            // tag OR the explicit root tag (rare but valid: nested runs).
            if (isRoot || PolyphonyTags.IsInScope(tags))
            {
                inScope.Add(new ScopeListItem
                {
                    Id = node.WorkItemId,
                    Title = node.Title,
                    Type = node.Type,
                    IsRoot = isRoot,
                });
            }
            else
            {
                outOfScope.Add(new ScopeListItem
                {
                    Id = node.WorkItemId,
                    Title = node.Title,
                    Type = node.Type,
                    IsRoot = false,
                });
            }
        }

        EmitList(new ScopeListResult
        {
            RootId = rootId,
            InScopeItems = inScope,
            OutOfScopeItems = outOfScope,
            InScopeCount = inScope.Count,
            OutOfScopeCount = outOfScope.Count,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Adds the bare <c>polyphony</c> tag to a work item. Idempotent — if the
    /// tag is already present, no ADO write is performed and
    /// <c>changed: false</c> is returned.
    /// </summary>
    /// <param name="workItem">ADO work item ID to tag.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("tag")]
    [VerbResult(typeof(ScopeMutationResult))]
    public Task<int> Tag(int workItem, CancellationToken ct = default) =>
        TagMutationAsync(workItem, PolyphonyTags.InScope, add: true, ct);

    /// <summary>
    /// Removes the bare <c>polyphony</c> tag from a work item. Idempotent —
    /// if the tag is absent, no ADO write is performed and
    /// <c>changed: false</c> is returned. Does NOT remove
    /// <c>polyphony:root</c> (use <c>polyphony root undeclare</c> for that
    /// in a future phase).
    /// </summary>
    /// <param name="workItem">ADO work item ID to untag.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("untag")]
    [VerbResult(typeof(ScopeMutationResult))]
    public Task<int> Untag(int workItem, CancellationToken ct = default) =>
        TagMutationAsync(workItem, PolyphonyTags.InScope, add: false, ct);

    internal async Task<int> TagMutationAsync(int workItem, string tag, bool add, CancellationToken ct)
    {
        await twig.SyncAsync(ct).ConfigureAwait(false);

        var item = await repository.GetByIdAsync(workItem, ct).ConfigureAwait(false);
        if (item is null)
        {
            EmitMutation(new ScopeMutationResult
            {
                WorkItemId = workItem,
                TagsBefore = [],
                TagsAfter = [],
                Error = $"Work item {workItem} not found",
            });
            return ExitCodes.RoutingFailure;
        }

        item.Fields.TryGetValue("System.Tags", out var raw);
        var before = TagSet.Parse(raw);
        var after = add ? before.Add(tag) : before.Remove(tag);
        var changed = !ReferenceEquals(before, after);

        if (changed)
        {
            try
            {
                await twig.PatchFieldsAsync(
                    workItem,
                    new Dictionary<string, string> { ["System.Tags"] = after.Format() },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EmitMutation(new ScopeMutationResult
                {
                    WorkItemId = workItem,
                    Changed = false,
                    TagsBefore = before.ToArray(),
                    TagsAfter = before.ToArray(),
                    Error = $"twig patch failed: {ex.Message}",
                });
                return ExitCodes.RoutingFailure;
            }
        }

        EmitMutation(new ScopeMutationResult
        {
            WorkItemId = workItem,
            Changed = changed,
            TagsBefore = before.ToArray(),
            TagsAfter = after.ToArray(),
        });
        return ExitCodes.Success;
    }

    private static IEnumerable<HierarchyResult> Flatten(HierarchyResult node)
    {
        yield return node;
        if (node.Children is null) yield break;
        foreach (var child in node.Children)
        {
            foreach (var sub in Flatten(child))
            {
                yield return sub;
            }
        }
    }

    private static void EmitCheck(ScopeCheckResult r) =>
        Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ScopeCheckResult));

    private static void EmitList(ScopeListResult r) =>
        Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ScopeListResult));

    private static void EmitMutation(ScopeMutationResult r) =>
        Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ScopeMutationResult));
}
