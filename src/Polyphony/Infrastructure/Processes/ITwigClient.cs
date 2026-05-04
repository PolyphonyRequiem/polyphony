using System.Text.Json.Nodes;

namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Typed wrapper over the <c>twig</c> CLI for side-effect operations
/// (<c>sync</c>, <c>set</c>, <c>state</c>, <c>patch</c>, <c>new</c>) and for
/// reads that need fresh CLI semantics (<c>show</c>, <c>tree</c>, <c>config</c>).
///
/// IMPORTANT: For pure cache reads, prefer <see cref="Twig.Domain.Interfaces.IWorkItemRepository"/>
/// and <see cref="Polyphony.Routing.HierarchyWalker"/>. Those bypass the
/// process boundary and are MUCH faster. Use this client when you need the
/// CLI's full semantics — e.g. <c>sync</c> flushes pending changes, refreshes
/// from ADO, hydrates ancestors, syncs tracked trees, and refreshes type and
/// field metadata; none of which the bare repository does.
///
/// All methods throw <see cref="ExternalToolException"/> when the underlying
/// twig invocation exits non-zero. Callers that need to tolerate failures
/// must wrap calls in try/catch.
/// </summary>
public interface ITwigClient
{
    /// <summary>
    /// <c>twig --version</c>. Returns the trimmed version string, or null if
    /// the binary is missing / not on PATH (no exception in that case).
    /// </summary>
    Task<string?> GetVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>twig sync --output json</c>. Flushes pending local changes and
    /// pulls fresh data from ADO. Fire-and-forget — the JSON output is
    /// discarded.
    /// </summary>
    Task SyncAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>twig show {id} --output json</c>. Returns the parsed JSON object,
    /// or null if the work item is not found (CLI exited non-zero).
    /// </summary>
    Task<JsonNode?> ShowAsync(int workItemId, CancellationToken ct = default);

    /// <summary>
    /// <c>twig show {id} --tree --output json</c>. Returns the tree-shaped
    /// JSON object, or null if not found.
    /// </summary>
    Task<JsonNode?> ShowTreeAsync(int workItemId, CancellationToken ct = default);

    /// <summary>
    /// <c>twig tree --depth {depth} --output json</c>. Operates on the active
    /// work item context (set via <see cref="SetActiveAsync"/>).
    /// </summary>
    Task<JsonNode?> TreeAsync(int depth, CancellationToken ct = default);

    /// <summary>
    /// <c>twig set {id} --output json</c>. Sets the active work item context.
    /// </summary>
    Task SetActiveAsync(int workItemId, CancellationToken ct = default);

    /// <summary>
    /// <c>twig state {stateName} --output json</c>. Transitions the ACTIVE
    /// work item to the named state. Caller must <see cref="SetActiveAsync"/>
    /// first.
    /// </summary>
    Task SetStateAsync(string stateName, CancellationToken ct = default);

    /// <summary>
    /// <c>twig patch --id {id} --json {fields}</c>. Patches the named fields
    /// (reference names like <c>System.Title</c>, <c>System.Tags</c>) on the
    /// given work item.
    /// </summary>
    Task PatchFieldsAsync(int workItemId, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default);

    /// <summary>
    /// <c>twig new --type {type} --title {title} --description {desc} --parent {parent} -o json</c>.
    /// Returns the parsed JSON of the newly created work item.
    /// </summary>
    Task<JsonNode> CreateChildAsync(int parentId, string type, string title, string description, CancellationToken ct = default);

    /// <summary>
    /// <c>twig config {key} --output json</c>. Returns the <c>info</c> field
    /// of the response — twig wraps scalar config values as <c>{"info":"..."}</c>.
    /// Returns null if the key is unset or the call fails.
    /// </summary>
    Task<string?> GetConfigValueAsync(string key, CancellationToken ct = default);
}
