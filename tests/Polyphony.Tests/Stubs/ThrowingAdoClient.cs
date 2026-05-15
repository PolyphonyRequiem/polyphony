using Polyphony.Infrastructure.AzureDevOps;

namespace Polyphony.Tests.Stubs;

/// <summary>
/// <see cref="IAdoClient"/> stub that throws on every call. Use this in
/// tests that exercise GitHub-only code paths (or tests that pre-date the
/// ADO refactor) so an accidental ADO branch surfaces as an immediate test
/// failure rather than silently null-pathing through the observer.
///
/// <para>
/// New ADO-aware tests should construct a purpose-built fake (the
/// <c>FakeAdoClient</c> classes inside the <c>PrCommands*AdoTests.cs</c>
/// files are the established pattern). This helper exists ONLY for the
/// "I need a non-null IAdoClient to satisfy the constructor" case.
/// </para>
/// </summary>
public sealed class ThrowingAdoClient : IAdoClient
{
    private static InvalidOperationException Throw([System.Runtime.CompilerServices.CallerMemberName] string member = "")
        => new($"ThrowingAdoClient.{member} should not be called from this test — pass a real fake if the ADO branch is in scope.");

    public Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default) => throw Throw();

    public Task<IReadOnlyList<AdoPullRequest>?> ListPullRequestsAsync(
        string organization, string project, string repository,
        AdoPullRequestStatus status = AdoPullRequestStatus.Active,
        string? sourceBranch = null,
        CancellationToken ct = default) => throw Throw();

    public Task<AdoPullRequest?> GetPullRequestAsync(
        string organization, string project, string repository, int pullRequestId,
        CancellationToken ct = default) => throw Throw();

    public Task<AdoPullRequest?> CreatePullRequestAsync(
        string organization, string project, string repository,
        string sourceBranch, string targetBranch, string title, string description,
        CancellationToken ct = default) => throw Throw();

    public Task<AdoPullRequestPollData?> GetPullRequestPollDataAsync(
        string organization, string project, string repositoryId, int pullRequestId,
        CancellationToken ct = default) => throw Throw();

    public Task<bool> SetPullRequestVoteAsync(
        string organization, string project, string repository,
        int pullRequestId, string reviewerId, int vote,
        CancellationToken ct = default) => throw Throw();

    public Task<AdoCompletePullRequestResult> CompletePullRequestAsync(
        string organization, string project, string repository,
        int pullRequestId, string lastMergeSourceCommitSha,
        AdoMergeStrategy mergeStrategy, bool deleteSourceBranch,
        CancellationToken ct = default) => throw Throw();

    public Task<AdoCreateThreadResult?> CreatePullRequestCommentThreadAsync(
        string organization, string project, string repository,
        int pullRequestId, string commentBody,
        CancellationToken ct = default) => throw Throw();

    public Task<IReadOnlyList<AdoPullRequestThread>?> ListPullRequestThreadsAsync(
        string organization, string project, string repository, int pullRequestId,
        CancellationToken ct = default) => throw Throw();

    public Task<AdoEvidenceFloorRead> GetPullRequestEvidenceFloorAsync(
        string organization, string project, string repository, int pullRequestId,
        CancellationToken ct = default) => throw Throw();

    public Task<IReadOnlyList<AdoPullRequestChangedFile>?> GetPullRequestFilesAsync(
        string organization, string project, string repository, int pullRequestId,
        CancellationToken ct = default) => throw Throw();

    public Task<bool> EditPullRequestBodyAsync(
        string organization, string project, string repository,
        int pullRequestId, string body,
        CancellationToken ct = default) => throw Throw();

    public Task<bool> ClosePullRequestAsync(
        string organization, string project, string repository,
        int pullRequestId, string commentBeforeClose,
        CancellationToken ct = default) => throw Throw();
}
