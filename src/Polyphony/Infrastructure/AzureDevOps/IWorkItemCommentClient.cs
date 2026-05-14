namespace Polyphony.Infrastructure.AzureDevOps;

/// <summary>
/// Typed abstraction over the Azure DevOps Work Item Comments REST API.
/// Separated from <see cref="IAdoClient"/> (which covers git/PR operations)
/// so test stubs stay focused: adding comment methods to the PR-oriented
/// <see cref="IAdoClient"/> would require updating 10+ <c>FakeAdoClient</c>
/// classes that only exercise PR verbs.
///
/// <para>Used by <c>polyphony reset run</c> to archive polyphony-authored
/// comments before clearing them.</para>
/// </summary>
public interface IWorkItemCommentClient
{
    /// <summary>
    /// List all discussion comments on a work item. Returns every page of
    /// results (the ADO comments endpoint is paginated via
    /// <c>continuationToken</c>).
    ///
    /// <para>Returns an empty list when the work item has no comments or
    /// when the work item does not exist (404). Throws on auth or server
    /// errors — see <see cref="IAdoClient"/> for the failure shape.</para>
    /// </summary>
    Task<IReadOnlyList<AdoWorkItemComment>> ListCommentsAsync(
        string organization,
        string project,
        int workItemId,
        CancellationToken ct = default);

    /// <summary>
    /// Delete a single comment from a work item.
    ///
    /// <para>Returns <c>true</c> when the comment was deleted (204) and
    /// <c>false</c> when the comment or work item does not exist (404).
    /// Throws on auth or server errors.</para>
    /// </summary>
    Task<bool> DeleteCommentAsync(
        string organization,
        string project,
        int workItemId,
        long commentId,
        CancellationToken ct = default);
}
