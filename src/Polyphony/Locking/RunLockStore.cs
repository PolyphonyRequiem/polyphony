using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Polyphony.Locking;

/// <summary>
/// Why a non-acquire attempt failed. Workflows route on this code,
/// not on exit code (acquire always exits 0).
/// </summary>
public enum AcquireFailureReason
{
    Held,
    Stale,
    Unreadable
}

/// <summary>
/// Why a release attempt did not remove the lock.
/// </summary>
public enum ReleaseFailureReason
{
    NotHeld,
    TokenMismatch,
    Unreadable
}

/// <summary>Outcome of an attempted acquire.</summary>
public sealed record AcquireOutcome
{
    public bool Acquired { get; init; }
    public RunLock? Lock { get; init; }
    public AcquireFailureReason? Reason { get; init; }
}

/// <summary>Outcome of an attempted token-based release.</summary>
public sealed record ReleaseOutcome
{
    public bool Released { get; init; }
    public ReleaseFailureReason? Reason { get; init; }
    public RunLock? ExistingLock { get; init; }
}

/// <summary>Outcome of a force-release.</summary>
public sealed record ForceReleaseOutcome
{
    public bool Released { get; init; }
    public bool WasHeld { get; init; }
    public RunLock? ExistingLock { get; init; }
}

/// <summary>Outcome of a status read.</summary>
public sealed record LockStatusOutcome
{
    public bool Exists { get; init; }
    public bool Valid { get; init; }
    public bool Stale { get; init; }
    public RunLock? Lock { get; init; }
    public long? TtlRemainingSeconds { get; init; }
    public string? ParseError { get; init; }
}

/// <summary>
/// File-system operations for the polyphony run lock.
///
/// <para><b>Acquire</b> is OS-atomic via <see cref="FileMode.CreateNew"/>
/// with <see cref="FileShare.None"/>. <b>Release</b> is rename-to-tombstone
/// then unlink so a stale releaser cannot delete a replacement lock created
/// by a different process between read and delete.</para>
///
/// <para>Stateless. Safe to register as a singleton.</para>
/// </summary>
public sealed class RunLockStore
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Try to atomically create a fresh lock file at <paramref name="path"/>.
    /// Returns <c>Acquired = true</c> with the persisted lock when the
    /// lock did not exist; otherwise inspects the existing file and
    /// classifies it as <c>held</c>, <c>stale</c>, or <c>unreadable</c>
    /// (which never auto-deletes per ADR Rev 4).
    /// </summary>
    public AcquireOutcome TryAcquire(string path, RunLock candidate, DateTime nowUtc)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                var yaml = Serializer.Serialize(candidate);
                using var writer = new StreamWriter(stream);
                writer.Write(yaml);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            return new AcquireOutcome { Acquired = true, Lock = candidate };
        }
        catch (IOException) when (File.Exists(path))
        {
            // Fall through to inspect the existing lock.
        }

        var (existing, _) = TryRead(path);
        if (existing is null)
        {
            return new AcquireOutcome
            {
                Acquired = false,
                Reason = AcquireFailureReason.Unreadable,
                Lock = null,
            };
        }

        var stale = nowUtc > existing.TtlUntil;
        return new AcquireOutcome
        {
            Acquired = false,
            Reason = stale ? AcquireFailureReason.Stale : AcquireFailureReason.Held,
            Lock = existing,
        };
    }

    /// <summary>
    /// Token-based release. Validates token, then renames the lock
    /// file to a tombstone before unlinking — guarantees that a stale
    /// releaser never removes a replacement lock acquired between
    /// read and unlink.
    /// </summary>
    public ReleaseOutcome TryRelease(string path, string lockToken)
    {
        if (!File.Exists(path))
        {
            return new ReleaseOutcome { Released = false, Reason = ReleaseFailureReason.NotHeld };
        }

        var (existing, _) = TryRead(path);
        if (existing is null)
        {
            return new ReleaseOutcome { Released = false, Reason = ReleaseFailureReason.Unreadable };
        }

        if (!string.Equals(existing.LockToken, lockToken, StringComparison.Ordinal))
        {
            return new ReleaseOutcome
            {
                Released = false,
                Reason = ReleaseFailureReason.TokenMismatch,
                ExistingLock = existing,
            };
        }

        var tombstone = $"{path}.releasing-{lockToken}";
        try
        {
            File.Move(path, tombstone);
        }
        catch (FileNotFoundException)
        {
            return new ReleaseOutcome { Released = false, Reason = ReleaseFailureReason.NotHeld };
        }

        try
        {
            File.Delete(tombstone);
        }
        catch
        {
            // Tombstone left behind; the original path is gone, so
            // the lock is effectively released. Tombstones are safe
            // to leave for next force-release / manual cleanup.
        }

        return new ReleaseOutcome { Released = true, ExistingLock = existing };
    }

    /// <summary>
    /// Unconditional release. Records whether a lock was present and
    /// whether it parsed cleanly for the operator's diagnostic
    /// benefit, then renames-and-deletes via a unique tombstone.
    /// </summary>
    public ForceReleaseOutcome ForceRelease(string path)
    {
        if (!File.Exists(path))
        {
            return new ForceReleaseOutcome { Released = true, WasHeld = false };
        }

        var (existing, _) = TryRead(path);

        var tombstone = $"{path}.force-releasing-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, tombstone);
        }
        catch (FileNotFoundException)
        {
            return new ForceReleaseOutcome { Released = true, WasHeld = false };
        }

        try
        {
            File.Delete(tombstone);
        }
        catch
        {
            // Tombstone left behind; safe to leave.
        }

        return new ForceReleaseOutcome
        {
            Released = true,
            WasHeld = true,
            ExistingLock = existing,
        };
    }

    /// <summary>
    /// Inspects the lock without mutating it. <see cref="LockStatusOutcome.Stale"/>
    /// is true when <c>now &gt; ttl_until</c>; the lock is still
    /// considered held until force-released per ADR Rev 4.
    /// </summary>
    public LockStatusOutcome Status(string path, DateTime nowUtc)
    {
        if (!File.Exists(path))
        {
            return new LockStatusOutcome { Exists = false, Valid = false };
        }

        var (existing, error) = TryRead(path);
        if (existing is null)
        {
            return new LockStatusOutcome
            {
                Exists = true,
                Valid = false,
                ParseError = error,
            };
        }

        var stale = nowUtc > existing.TtlUntil;
        var ttlRemaining = stale ? 0L : (long)(existing.TtlUntil - nowUtc).TotalSeconds;

        return new LockStatusOutcome
        {
            Exists = true,
            Valid = true,
            Stale = stale,
            Lock = existing,
            TtlRemainingSeconds = ttlRemaining,
        };
    }

    private static (RunLock?, string?) TryRead(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, "lock file is empty");
        }

        try
        {
            var lockObj = Deserializer.Deserialize<RunLock>(text);
            if (lockObj is null || lockObj.Schema == 0 || string.IsNullOrEmpty(lockObj.LockToken))
            {
                return (null, "lock file is missing required fields");
            }

            return (lockObj, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
