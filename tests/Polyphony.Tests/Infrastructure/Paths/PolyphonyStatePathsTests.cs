using Polyphony.Infrastructure.Paths;
using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Paths;

public sealed class PolyphonyStatePathsTests
{
    private static PolyphonyStatePaths Make(string? commonDir)
        => new(new StubGitClient(commonDir));

    // ── Layout shape ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetStateBaseAsync_PutsPolyphonyUnderCommonDir()
    {
        var paths = Make("/repo/.git");
        (await paths.GetStateBaseAsync())
            .ShouldBe(Path.Combine("/repo/.git", "polyphony"));
    }

    [Fact]
    public async Task GetStateRootAsync_NestsRootIdUnderStateBase()
    {
        var paths = Make("/repo/.git");
        (await paths.GetStateRootAsync(3067))
            .ShouldBe(Path.Combine("/repo/.git", "polyphony", "3067"));
    }

    [Fact]
    public async Task GetManifestPathAsync_LandsAtRunYamlUnderStateRoot()
    {
        var paths = Make("/repo/.git");
        (await paths.GetManifestPathAsync(3067))
            .ShouldBe(Path.Combine("/repo/.git", "polyphony", "3067", "run.yaml"));
    }

    [Fact]
    public async Task GetLockPathAsync_LandsAtRunLockUnderLocksSubdir()
    {
        var paths = Make("/repo/.git");
        (await paths.GetLockPathAsync(3067))
            .ShouldBe(Path.Combine("/repo/.git", "polyphony", "3067", "locks", "run.lock"));
    }

    // ── Convergence + isolation invariants ───────────────────────────────

    [Fact]
    public async Task SameRoot_AcrossWorktrees_ResolvesToSamePath()
    {
        // Both the main worktree and a linked worktree report the same
        // common dir (the main repo's .git). That convergence is the
        // entire reason for the move — every linked worktree of the same
        // clone sees the same per-root state.
        var mainPaths = Make("/repo/.git");
        var linkedPaths = Make("/repo/.git");

        (await mainPaths.GetManifestPathAsync(3067))
            .ShouldBe(await linkedPaths.GetManifestPathAsync(3067));
        (await mainPaths.GetLockPathAsync(3067))
            .ShouldBe(await linkedPaths.GetLockPathAsync(3067));
    }

    [Fact]
    public async Task DifferentRoots_InSameClone_ResolveToDistinctPaths()
    {
        // The per-root subdirectory is what isolates concurrent runs of
        // different roots within the same clone. Without it, AB#3066's
        // manifest would be overwritten by AB#3067's start-up.
        var paths = Make("/repo/.git");

        var path3066 = await paths.GetManifestPathAsync(3066);
        var path3067 = await paths.GetManifestPathAsync(3067);

        path3066.ShouldNotBe(path3067);
        Path.GetDirectoryName(path3066).ShouldNotBe(Path.GetDirectoryName(path3067));
    }

    // ── Failure modes ────────────────────────────────────────────────────

    [Fact]
    public async Task GetStateBaseAsync_NotInGitRepo_Throws()
    {
        var paths = Make(null);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => paths.GetStateBaseAsync());
        ex.Message.ShouldContain("--git-common-dir");
    }

    [Fact]
    public async Task GetManifestPathAsync_NotInGitRepo_Throws()
    {
        var paths = Make("");
        await Should.ThrowAsync<InvalidOperationException>(() => paths.GetManifestPathAsync(3067));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task GetStateRootAsync_NonPositiveRootId_Throws(int rootId)
    {
        var paths = Make("/repo/.git");
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => paths.GetStateRootAsync(rootId));
    }

    // ── Stub ─────────────────────────────────────────────────────────────

    private sealed class StubGitClient : IGitClient
    {
        private readonly string? _commonDir;
        public StubGitClient(string? commonDir) { _commonDir = commonDir; }

        public Task<string?> GetCommonDirAsync(CancellationToken ct = default) => Task.FromResult(_commonDir);
        public Task<bool> IsBareRepositoryAsync(string commonDir, CancellationToken ct = default) => throw new NotSupportedException();

        // Unused by PolyphonyStatePaths; throw to surface accidental coupling.
        public Task<string?> GetTopLevelAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetCurrentBranchAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListRemoteBranchesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListLocalBranchesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProcessResult> DeleteLocalBranchAsync(string branch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> LsRemoteHeadsAsync(string remote, string pattern, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> RevParseLocalBranchAsync(string branch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CheckoutAsync(string branch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateBranchAsync(string branch, string? startPoint = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CheckoutTrackingAsync(string branch, string remote = "origin", CancellationToken ct = default) => throw new NotSupportedException();
        public Task PushAsync(string branch, string remote = "origin", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProcessResult> DeleteRemoteBranchAsync(string remote, string branch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task FetchAsync(string remote, string refspec, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetStatusAsync(string workingDirectory, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetInProgressOperationAsync(string workingDirectory, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StageAsync(string pathspec, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CommitAsync(string message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ResetHardAsync(string refspec, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> ShowFileAtRefAsync(string refspec, string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProcessResult> WorktreeAddAsync(string branch, string path, string? gitRef, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProcessResult> WorktreeAddAttachAsync(string branch, string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProcessResult> WorktreeRemoveAsync(string path, bool force, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProcessResult> WorktreeListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RebaseOutcome> RebaseOntoAsync(string newBase, string oldBase, string head, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MergeBaseAsync(string a, string b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsAncestorAsync(string maybeAncestor, string descendant, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProcessResult> PushHeadWithLeaseAsync(string remote, string branch, string expectedRemoteSha, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
