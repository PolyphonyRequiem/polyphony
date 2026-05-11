using System.Text.Json;
using Polyphony;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Locking;
using Polyphony.Models;
using Polyphony.Tests.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Locking;

public sealed class LockCommandsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"polyphony-lockcmd-tests-{Guid.NewGuid():N}");

    private readonly LockCommands _sut;

    public LockCommandsTests()
    {
        Directory.CreateDirectory(_dir);
        var store = new RunLockStore();
        var resolver = new RunLockPathResolver(new FakeGitClient(_dir));
        _sut = new LockCommands(store, resolver);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch { /* tombstones may linger; best-effort */ }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static async Task<(int ExitCode, string Output)> CaptureAsync(Func<Task<int>> action)
    {
        await ConsoleTestLock.AsyncLock.WaitAsync();
        try
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var exitCode = await action();
                return (exitCode, writer.ToString().Trim());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }
    }

    [Fact]
    public async Task Acquire_OnEmptyDir_Succeeds()
    {
        var path = PathOf("run-1234.lock");

        var (exit, output) = await CaptureAsync(() => _sut.Acquire(rootId: 1234, ttlHours: 24, by: "alice", path: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<AcquireLockResult>(output, PolyphonyJsonContext.Default.AcquireLockResult)!;
        result.Acquired.ShouldBeTrue();
        result.LockToken.ShouldNotBeNullOrEmpty();
        result.Lock!.AcquiredBy.ShouldBe("alice");
        result.Lock!.RootId.ShouldBe(1234);
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task Acquire_WhenHeld_ReturnsHeldExit0()
    {
        var path = PathOf("run-1234.lock");
        await CaptureAsync(() => _sut.Acquire(rootId: 1234, ttlHours: 24, by: "first", path: path));

        var (exit, output) = await CaptureAsync(() => _sut.Acquire(rootId: 1234, ttlHours: 24, by: "second", path: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<AcquireLockResult>(output, PolyphonyJsonContext.Default.AcquireLockResult)!;
        result.Acquired.ShouldBeFalse();
        result.Reason.ShouldBe("held");
        result.ExistingLock!.AcquiredBy.ShouldBe("first");
    }

    [Fact]
    public async Task Acquire_WhenStale_ReturnsStaleExit0()
    {
        var path = PathOf("run-1234.lock");
        // Acquire with 0-hour TTL would fail validation; manually plant a stale lock instead.
        var stale = new RunLock
        {
            Schema = 1,
            RootId = 1234,
            AcquiredAt = DateTime.UtcNow.AddDays(-2),
            AcquiredBy = "ancient",
            Host = "old",
            Pid = 1,
            LockToken = "stale-tok",
            TtlUntil = DateTime.UtcNow.AddDays(-1),
            RepoRoot = _dir,
            PolyphonyVersion = "0.0.0",
            CommandLine = "",
        };
        new RunLockStore().TryAcquire(path, stale, stale.AcquiredAt);

        var (exit, output) = await CaptureAsync(() => _sut.Acquire(rootId: 1234, ttlHours: 24, path: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<AcquireLockResult>(output, PolyphonyJsonContext.Default.AcquireLockResult)!;
        result.Acquired.ShouldBeFalse();
        result.Reason.ShouldBe("stale");
        result.ExistingLock!.LockToken.ShouldBe("stale-tok");
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task Acquire_WhenMalformed_ReturnsUnreadableExit0()
    {
        var path = PathOf("run-1234.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ malformed: yaml :: junk");

        var (exit, output) = await CaptureAsync(() => _sut.Acquire(rootId: 1234, path: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<AcquireLockResult>(output, PolyphonyJsonContext.Default.AcquireLockResult)!;
        result.Acquired.ShouldBeFalse();
        result.Reason.ShouldBe("unreadable");
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task Acquire_RejectsNonPositiveRootId()
    {
        var (exit, output) = await CaptureAsync(() => _sut.Acquire(rootId: 0));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize<AcquireLockResult>(output, PolyphonyJsonContext.Default.AcquireLockResult)!;
        result.Acquired.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Acquire_RejectsNonPositiveTtl()
    {
        var (exit, _) = await CaptureAsync(() => _sut.Acquire(rootId: 1, ttlHours: 0));
        exit.ShouldBe(ExitCodes.ConfigError);
    }

    [Fact]
    public async Task Acquire_DefaultPath_UsesGitCommonDirLayout()
    {
        var (exit, output) = await CaptureAsync(() => _sut.Acquire(rootId: 1234, by: "alice"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<AcquireLockResult>(output, PolyphonyJsonContext.Default.AcquireLockResult)!;
        result.Acquired.ShouldBeTrue();
        // Rev 4.2: lock lives under <git-common-dir>/polyphony/<root_id>/locks/run.lock.
        // FakeGitClient.GetCommonDirAsync returns Path.Combine(_topLevel, ".git").
        var expected = Path.Combine(_dir, ".git", "polyphony", "1234", "locks", "run.lock");
        result.Path.ShouldBe(expected);
        File.Exists(expected).ShouldBeTrue();
    }

    [Fact]
    public async Task Release_WithMatchingToken_Succeeds()
    {
        var path = PathOf("run-1234.lock");
        var (_, acquireOut) = await CaptureAsync(() => _sut.Acquire(rootId: 1234, by: "alice", path: path));
        var token = JsonSerializer.Deserialize<AcquireLockResult>(acquireOut, PolyphonyJsonContext.Default.AcquireLockResult)!.LockToken!;

        var (exit, output) = await CaptureAsync(() => _sut.Release(rootId: 1234, lockToken: token, path: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<ReleaseLockResult>(output, PolyphonyJsonContext.Default.ReleaseLockResult)!;
        result.Released.ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task Release_WithBadToken_ExitsNonZero()
    {
        var path = PathOf("run-1234.lock");
        await CaptureAsync(() => _sut.Acquire(rootId: 1234, path: path));

        var (exit, output) = await CaptureAsync(() => _sut.Release(rootId: 1234, lockToken: "wrong", path: path));

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var result = JsonSerializer.Deserialize<ReleaseLockResult>(output, PolyphonyJsonContext.Default.ReleaseLockResult)!;
        result.Released.ShouldBeFalse();
        result.Reason.ShouldBe("token_mismatch");
        result.ExistingLock.ShouldNotBeNull();
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task Release_WhenMissing_IsIdempotentExit0()
    {
        var path = PathOf("run-1234.lock");

        var (exit, output) = await CaptureAsync(() => _sut.Release(rootId: 1234, lockToken: "any", path: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<ReleaseLockResult>(output, PolyphonyJsonContext.Default.ReleaseLockResult)!;
        result.Released.ShouldBeFalse();
        result.Reason.ShouldBe("not_held");
    }

    [Fact]
    public async Task Release_RejectsEmptyToken()
    {
        var (exit, output) = await CaptureAsync(() => _sut.Release(rootId: 1234, lockToken: ""));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("lock release");
        envelope.MissingArgs.ShouldContain("--lock-token");
    }

    [Fact]
    public async Task ForceRelease_WhenHeld_RemovesAndReportsLock()
    {
        var path = PathOf("run-1234.lock");
        await CaptureAsync(() => _sut.Acquire(rootId: 1234, by: "alice", path: path));

        var (exit, output) = await CaptureAsync(() => _sut.ForceRelease(rootId: 1234, path: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<ForceReleaseLockResult>(output, PolyphonyJsonContext.Default.ForceReleaseLockResult)!;
        result.Released.ShouldBeTrue();
        result.WasHeld.ShouldBeTrue();
        result.ExistingLock!.AcquiredBy.ShouldBe("alice");
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task ForceRelease_WhenMissing_ReportsNotHeld()
    {
        var path = PathOf("run-1234.lock");

        var (exit, output) = await CaptureAsync(() => _sut.ForceRelease(rootId: 1234, path: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<ForceReleaseLockResult>(output, PolyphonyJsonContext.Default.ForceReleaseLockResult)!;
        result.Released.ShouldBeTrue();
        result.WasHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task Status_WhenLive_ReportsTtl()
    {
        var path = PathOf("run-1234.lock");
        await CaptureAsync(() => _sut.Acquire(rootId: 1234, ttlHours: 24, path: path));

        var (exit, output) = await CaptureAsync(() => _sut.Status(rootId: 1234, path: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<LockStatusResult>(output, PolyphonyJsonContext.Default.LockStatusResult)!;
        result.Exists.ShouldBeTrue();
        result.Valid.ShouldBeTrue();
        result.Stale.ShouldBeFalse();
        result.TtlRemainingSeconds.ShouldNotBeNull();
        result.TtlRemainingSeconds!.Value.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Status_WhenMissing_ReportsExistsFalse()
    {
        var path = PathOf("run-1234.lock");

        var (exit, output) = await CaptureAsync(() => _sut.Status(rootId: 1234, path: path));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize<LockStatusResult>(output, PolyphonyJsonContext.Default.LockStatusResult)!;
        result.Exists.ShouldBeFalse();
        result.Valid.ShouldBeFalse();
    }

    [Fact]
    public async Task TwoDifferentRoots_DoNotConflict()
    {
        var pathA = PathOf("run-100.lock");
        var pathB = PathOf("run-200.lock");

        var (exitA, _) = await CaptureAsync(() => _sut.Acquire(rootId: 100, by: "alice", path: pathA));
        var (exitB, _) = await CaptureAsync(() => _sut.Acquire(rootId: 200, by: "bob", path: pathB));

        exitA.ShouldBe(ExitCodes.Success);
        exitB.ShouldBe(ExitCodes.Success);
        File.Exists(pathA).ShouldBeTrue();
        File.Exists(pathB).ShouldBeTrue();
    }

    private sealed class FakeGitClient : IGitClient
    {
        private readonly string _topLevel;
        public FakeGitClient(string topLevel) { _topLevel = topLevel; }
        public Task<string?> GetTopLevelAsync(CancellationToken ct = default) => Task.FromResult<string?>(_topLevel);
        public Task<string?> GetCommonDirAsync(CancellationToken ct = default) => Task.FromResult<string?>(Path.Combine(_topLevel, ".git"));
        public Task<bool> IsBareRepositoryAsync(string commonDir, CancellationToken ct = default) => Task.FromResult(false);
        public Task<string?> GetCurrentBranchAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetRemoteUrlAsync(string remote = "origin", CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> ListRemoteBranchesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> LsRemoteHeadsAsync(string remote, string pattern, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> RevParseLocalBranchAsync(string branch, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task CheckoutAsync(string branch, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateBranchAsync(string branch, string? startPoint = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task CheckoutTrackingAsync(string branch, string remote = "origin", CancellationToken ct = default) => Task.CompletedTask;
        public Task PushAsync(string branch, string remote = "origin", CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProcessResult> DeleteRemoteBranchAsync(string remote, string branch, CancellationToken ct = default) => Task.FromResult(new ProcessResult(0, "", ""));
        public Task FetchAsync(string remote, string refspec, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetStatusAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetStatusAsync(string workingDirectory, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> GetInProgressOperationAsync(string workingDirectory, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task StageAsync(string pathspec, CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetHardAsync(string refspec, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> ShowFileAtRefAsync(string refspec, string path, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<ProcessResult> WorktreeAddAsync(string branch, string path, string? gitRef, CancellationToken ct = default) => Task.FromResult(new ProcessResult(0, "", ""));
        public Task<ProcessResult> WorktreeAddAttachAsync(string branch, string path, CancellationToken ct = default) => Task.FromResult(new ProcessResult(0, "", ""));
        public Task<ProcessResult> WorktreeRemoveAsync(string path, bool force, CancellationToken ct = default) => Task.FromResult(new ProcessResult(0, "", ""));
        public Task<ProcessResult> WorktreeListAsync(CancellationToken ct = default) => Task.FromResult(new ProcessResult(0, "", ""));
        public Task<RebaseOutcome> RebaseOntoAsync(string newBase, string oldBase, string head, CancellationToken ct = default)
            => Task.FromResult<RebaseOutcome>(new RebaseOutcome.Failed("not implemented"));
        public Task<string?> MergeBaseAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<bool> IsAncestorAsync(string maybeAncestor, string descendant, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ProcessResult> PushHeadWithLeaseAsync(string remote, string branch, string expectedRemoteSha, CancellationToken ct = default)
            => Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }
}
