using Polyphony.Locking;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Locking;

public sealed class RunLockStoreTests : IDisposable
{
    private readonly RunLockStore _sut = new();
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"polyphony-lock-tests-{Guid.NewGuid():N}");

    private string PathOf(string name) => Path.Combine(_dir, name);

    public RunLockStoreTests()
    {
        Directory.CreateDirectory(_dir);
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

    private static RunLock Candidate(int rootId = 1234, string token = "tok-1", DateTime? acquiredAt = null, int ttlHours = 24)
    {
        var at = acquiredAt ?? new DateTime(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc);
        return new RunLock
        {
            Schema = 1,
            RootId = rootId,
            AcquiredAt = at,
            AcquiredBy = "test",
            Host = "host",
            Pid = 4242,
            LockToken = token,
            TtlUntil = at.AddHours(ttlHours),
            RepoRoot = @"C:\repo",
            PolyphonyVersion = "1.2.3",
            CommandLine = "polyphony lock acquire",
        };
    }

    [Fact]
    public void TryAcquire_OnEmptyDir_ReturnsAcquired()
    {
        var path = PathOf("run-1234.lock");

        var outcome = _sut.TryAcquire(path, Candidate(), DateTime.UtcNow);

        outcome.Acquired.ShouldBeTrue();
        outcome.Lock.ShouldNotBeNull();
        outcome.Reason.ShouldBeNull();
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_CreatesParentDirectory()
    {
        var path = Path.Combine(_dir, "deep", "nested", "run-1234.lock");

        var outcome = _sut.TryAcquire(path, Candidate(), DateTime.UtcNow);

        outcome.Acquired.ShouldBeTrue();
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_WhenHeldAndLive_ReturnsHeld()
    {
        var path = PathOf("run-1234.lock");
        var now = new DateTime(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc);
        _sut.TryAcquire(path, Candidate(token: "first", acquiredAt: now), now);

        var outcome = _sut.TryAcquire(path, Candidate(token: "second", acquiredAt: now.AddSeconds(5)), now.AddSeconds(5));

        outcome.Acquired.ShouldBeFalse();
        outcome.Reason.ShouldBe(AcquireFailureReason.Held);
        outcome.Lock!.LockToken.ShouldBe("first");
    }

    [Fact]
    public void TryAcquire_WhenStale_ReturnsStaleNeverDeletes()
    {
        var path = PathOf("run-1234.lock");
        var acquiredAt = new DateTime(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc);
        _sut.TryAcquire(path, Candidate(token: "first", acquiredAt: acquiredAt, ttlHours: 1), acquiredAt);

        var farFuture = acquiredAt.AddHours(48);

        var outcome = _sut.TryAcquire(path, Candidate(token: "second", acquiredAt: farFuture), farFuture);

        outcome.Acquired.ShouldBeFalse();
        outcome.Reason.ShouldBe(AcquireFailureReason.Stale);
        outcome.Lock!.LockToken.ShouldBe("first");
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_WhenMalformed_ReturnsUnreadableNeverDeletes()
    {
        var path = PathOf("run-1234.lock");
        File.WriteAllText(path, "not: yaml: this: malformed: ::");

        var outcome = _sut.TryAcquire(path, Candidate(), DateTime.UtcNow);

        outcome.Acquired.ShouldBeFalse();
        outcome.Reason.ShouldBe(AcquireFailureReason.Unreadable);
        outcome.Lock.ShouldBeNull();
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_WhenEmpty_ReturnsUnreadable()
    {
        var path = PathOf("run-1234.lock");
        File.WriteAllText(path, "");

        var outcome = _sut.TryAcquire(path, Candidate(), DateTime.UtcNow);

        outcome.Acquired.ShouldBeFalse();
        outcome.Reason.ShouldBe(AcquireFailureReason.Unreadable);
    }

    [Fact]
    public void TryRelease_WhenTokenMatches_RemovesFile()
    {
        var path = PathOf("run-1234.lock");
        _sut.TryAcquire(path, Candidate(token: "tok-good"), DateTime.UtcNow);

        var outcome = _sut.TryRelease(path, "tok-good");

        outcome.Released.ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void TryRelease_WhenTokenMismatch_KeepsFile()
    {
        var path = PathOf("run-1234.lock");
        _sut.TryAcquire(path, Candidate(token: "tok-good"), DateTime.UtcNow);

        var outcome = _sut.TryRelease(path, "wrong");

        outcome.Released.ShouldBeFalse();
        outcome.Reason.ShouldBe(ReleaseFailureReason.TokenMismatch);
        outcome.ExistingLock!.LockToken.ShouldBe("tok-good");
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void TryRelease_WhenFileMissing_ReturnsNotHeld()
    {
        var path = PathOf("run-1234.lock");

        var outcome = _sut.TryRelease(path, "any");

        outcome.Released.ShouldBeFalse();
        outcome.Reason.ShouldBe(ReleaseFailureReason.NotHeld);
    }

    [Fact]
    public void TryRelease_WhenMalformed_ReturnsUnreadable()
    {
        var path = PathOf("run-1234.lock");
        File.WriteAllText(path, "garbage::malformed");

        var outcome = _sut.TryRelease(path, "any");

        outcome.Released.ShouldBeFalse();
        outcome.Reason.ShouldBe(ReleaseFailureReason.Unreadable);
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void TryRelease_DoesNotLeakTombstones()
    {
        var path = PathOf("run-1234.lock");
        _sut.TryAcquire(path, Candidate(token: "tok-good"), DateTime.UtcNow);

        _sut.TryRelease(path, "tok-good");

        Directory.GetFiles(_dir).ShouldBeEmpty();
    }

    [Fact]
    public void ForceRelease_WhenHeld_RemovesAndReportsExistingLock()
    {
        var path = PathOf("run-1234.lock");
        _sut.TryAcquire(path, Candidate(token: "abc"), DateTime.UtcNow);

        var outcome = _sut.ForceRelease(path);

        outcome.Released.ShouldBeTrue();
        outcome.WasHeld.ShouldBeTrue();
        outcome.ExistingLock!.LockToken.ShouldBe("abc");
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void ForceRelease_WhenMissing_ReturnsReleasedButNotHeld()
    {
        var path = PathOf("run-1234.lock");

        var outcome = _sut.ForceRelease(path);

        outcome.Released.ShouldBeTrue();
        outcome.WasHeld.ShouldBeFalse();
    }

    [Fact]
    public void ForceRelease_WhenMalformed_RemovesAnyway()
    {
        var path = PathOf("run-1234.lock");
        File.WriteAllText(path, "::garbage");

        var outcome = _sut.ForceRelease(path);

        outcome.Released.ShouldBeTrue();
        outcome.WasHeld.ShouldBeTrue();
        outcome.ExistingLock.ShouldBeNull();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void Status_WhenMissing_ReturnsExistsFalse()
    {
        var status = _sut.Status(PathOf("run-1234.lock"), DateTime.UtcNow);

        status.Exists.ShouldBeFalse();
        status.Valid.ShouldBeFalse();
    }

    [Fact]
    public void Status_WhenLive_ReportsTtlRemaining()
    {
        var path = PathOf("run-1234.lock");
        var acquiredAt = new DateTime(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc);
        _sut.TryAcquire(path, Candidate(token: "abc", acquiredAt: acquiredAt, ttlHours: 24), acquiredAt);

        var status = _sut.Status(path, acquiredAt.AddHours(1));

        status.Exists.ShouldBeTrue();
        status.Valid.ShouldBeTrue();
        status.Stale.ShouldBeFalse();
        status.TtlRemainingSeconds.ShouldBe((long)TimeSpan.FromHours(23).TotalSeconds);
        status.Lock!.LockToken.ShouldBe("abc");
    }

    [Fact]
    public void Status_WhenStale_ReportsStale()
    {
        var path = PathOf("run-1234.lock");
        var acquiredAt = new DateTime(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc);
        _sut.TryAcquire(path, Candidate(acquiredAt: acquiredAt, ttlHours: 1), acquiredAt);

        var status = _sut.Status(path, acquiredAt.AddHours(48));

        status.Exists.ShouldBeTrue();
        status.Valid.ShouldBeTrue();
        status.Stale.ShouldBeTrue();
        status.TtlRemainingSeconds.ShouldBe(0);
    }

    [Fact]
    public void Status_WhenMalformed_ReportsParseError()
    {
        var path = PathOf("run-1234.lock");
        File.WriteAllText(path, "garbage:: yaml :: here");

        var status = _sut.Status(path, DateTime.UtcNow);

        status.Exists.ShouldBeTrue();
        status.Valid.ShouldBeFalse();
        status.ParseError.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void TryAcquire_RoundTripsAllFields()
    {
        var path = PathOf("run-1234.lock");
        var at = new DateTime(2026, 5, 6, 10, 0, 0, DateTimeKind.Utc);
        var candidate = new RunLock
        {
            Schema = 1,
            RootId = 9999,
            AcquiredAt = at,
            AcquiredBy = "alice",
            Host = "host-a",
            Pid = 12345,
            LockToken = "f00-ba4",
            TtlUntil = at.AddHours(12),
            RepoRoot = @"C:\my\repo",
            PolyphonyVersion = "1.2.3-beta",
            CommandLine = "polyphony lock acquire --root-id 9999",
        };

        _sut.TryAcquire(path, candidate, at);
        var status = _sut.Status(path, at);

        var lockFile = status.Lock!;
        lockFile.Schema.ShouldBe(1);
        lockFile.RootId.ShouldBe(9999);
        lockFile.AcquiredAt.ShouldBe(at);
        lockFile.AcquiredBy.ShouldBe("alice");
        lockFile.Host.ShouldBe("host-a");
        lockFile.Pid.ShouldBe(12345);
        lockFile.LockToken.ShouldBe("f00-ba4");
        lockFile.TtlUntil.ShouldBe(at.AddHours(12));
        lockFile.RepoRoot.ShouldBe(@"C:\my\repo");
        lockFile.PolyphonyVersion.ShouldBe("1.2.3-beta");
        lockFile.CommandLine.ShouldBe("polyphony lock acquire --root-id 9999");
    }

    [Fact]
    public void TryRelease_DoesNotDeleteReplacementLockUnderRace()
    {
        // Reproduce the rubber-duck-identified race:
        //   1. Holder A reads its lock, validates token.
        //   2. A separate force-release removes A's lock.
        //   3. Holder B acquires a new lock (different token).
        //   4. Stale releaser A continues with its release.
        //   5. A must NOT delete B's replacement lock.
        var path = PathOf("run-1234.lock");
        _sut.TryAcquire(path, Candidate(token: "tok-A"), DateTime.UtcNow);
        _sut.ForceRelease(path);
        _sut.TryAcquire(path, Candidate(token: "tok-B"), DateTime.UtcNow);

        // A still thinks it owns "tok-A" and tries to release.
        var outcome = _sut.TryRelease(path, "tok-A");

        outcome.Released.ShouldBeFalse();
        outcome.Reason.ShouldBe(ReleaseFailureReason.TokenMismatch);
        File.Exists(path).ShouldBeTrue();
        var status = _sut.Status(path, DateTime.UtcNow);
        status.Lock!.LockToken.ShouldBe("tok-B");
    }
}
