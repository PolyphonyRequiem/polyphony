using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
using Polyphony.Tagging;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Root verbs (<c>polyphony root ...</c>). Manage the <c>polyphony:root</c>
/// marker tag and resolve a work item's nearest root ancestor.
/// Authoritative spec: <c>docs/reference/polyphony-tags.md</c>.
///
/// <list type="bullet">
///   <item><c>declare</c> — stamp <c>polyphony:root</c> on an item (idempotent).</item>
///   <item><c>resolve</c> — walk ancestors to find the nearest root tag; surface fallback-required when none found.</item>
/// </list>
/// </summary>
[VerbGroup("root")]
public sealed class RootCommands(
    ITwigClient twig,
    IWorkItemRepository repository,
    ScopeCommands scopeCommands,
    RunContext? runContext = null,
    JournaledActionDecorator? journalDecorator = null)
{
    private readonly RunContext _runContext = JournalCommandSupport.ResolveRunContext(runContext);
    private readonly JournaledActionDecorator _journalDecorator = JournalCommandSupport.ResolveDecorator(journalDecorator);
    /// <summary>
    /// Stamps <c>polyphony:root</c> on the work item. Idempotent.
    /// </summary>
    /// <param name="workItem">ADO work item ID to declare as root.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("declare")]
    [VerbResult(typeof(ScopeMutationResult))]
    [JournaledAction(Action = "root_declare")]
    [MutatesResource(ResourceKind.AdoWorkItemTag)]
    [MayObserveResource(ResourceKind.AdoWorkItem)]
    public Task<int> Declare(int workItem = RequiredInput.MissingInt, CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("root declare",
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return Task.FromResult(halt);

        return JournalCommandSupport.RunWithCapturedResultAsync<ScopeMutationResult, TagMutationPayload>(
            _journalDecorator,
            _runContext,
            "root_declare",
            JournalCommandSupport.WorkItemTarget(workItem),
            innerCt => scopeCommands.TagMutationCoreAsync(workItem, PolyphonyTags.Root, add: true, innerCt),
            PolyphonyJsonContext.Default.ScopeMutationResult,
            (exitCode, result) => new TagMutationPayload
            {
                WorkItemId = workItem,
                Tag = PolyphonyTags.Root,
                EnsurePresent = true,
                TagsBefore = result?.TagsBefore ?? [],
                TagsAfter = result?.TagsAfter ?? [],
                Succeeded = exitCode == ExitCodes.Success && string.IsNullOrEmpty(result?.Error),
                WasMutated = result?.Changed ?? false,
                AlreadyInDesiredState = !(result?.Changed ?? false),
                Error = result?.Error,
            },
            PolyphonyJsonContext.Default.TagMutationPayload,
            payload => payload.Succeeded,
            payload => payload.WasMutated,
            ScopeCommands.SelectTagMutationEffects,
            ct,
            rootId: workItem,
            workItemId: workItem);
    }

    /// <summary>
    /// Walks ancestors of <paramref name="workItem"/> (inclusive) to find the
    /// nearest item carrying <see cref="PolyphonyTags.Root"/>. Emits a JSON
    /// payload with the resolved root id, the chain of ancestors walked, and
    /// a <c>fallback_required</c> flag for the workflow to fire the root
    /// fallback gate.
    /// </summary>
    /// <param name="workItem">ADO work item ID to resolve.</param>
    /// <param name="maxAncestorWalk">Max ancestors to walk before giving up (default 32).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("resolve")]
    [VerbResult(typeof(RootResolveResult))]
    public async Task<int> Resolve(int workItem = RequiredInput.MissingInt, int maxAncestorWalk = 32, CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("root resolve",
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return halt;

        await twig.SyncAsync(ct).ConfigureAwait(false);

        var walked = new List<int>();
        var seen = new HashSet<int>();
        int? cursor = workItem;

        while (cursor is int currentId && walked.Count < maxAncestorWalk)
        {
            if (!seen.Add(currentId))
            {
                EmitResolve(new RootResolveResult
                {
                    WorkItemId = workItem,
                    AncestorsWalked = [.. walked],
                    FallbackRequired = true,
                    Error = $"Cycle detected at id {currentId}",
                });
                return ExitCodes.RoutingFailure;
            }

            walked.Add(currentId);

            var item = await repository.GetByIdAsync(currentId, ct).ConfigureAwait(false);
            if (item is null)
            {
                EmitResolve(new RootResolveResult
                {
                    WorkItemId = workItem,
                    AncestorsWalked = [.. walked],
                    FallbackRequired = true,
                    Error = $"Work item {currentId} not found",
                });
                return ExitCodes.RoutingFailure;
            }

            item.Fields.TryGetValue("System.Tags", out var raw);
            var tags = TagSet.Parse(raw);
            if (PolyphonyTags.IsRoot(tags))
            {
                EmitResolve(new RootResolveResult
                {
                    WorkItemId = workItem,
                    ResolvedRootId = currentId,
                    AncestorsWalked = [.. walked],
                    FallbackRequired = false,
                });
                return ExitCodes.Success;
            }

            cursor = item.ParentId;
        }

        // Walked off the top of the tree (no parent) OR hit max-ancestor cap
        // without finding a root tag. Either way, the fallback gate must fire.
        EmitResolve(new RootResolveResult
        {
            WorkItemId = workItem,
            AncestorsWalked = [.. walked],
            FallbackRequired = true,
        });
        return ExitCodes.Success;
    }

    private static void EmitResolve(RootResolveResult r) =>
        Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.RootResolveResult));
}
