using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Locking;
using Polyphony.Models;

namespace Polyphony.Commands;

/// <summary>
/// Lock verbs (<c>polyphony lock ...</c>). Phase 4b PR D1b — same-root
/// run lock at <c>&lt;repoRoot&gt;/.polyphony/locks/run-{root_id}.lock</c>
/// per the Rev 4 branch-model ADR.
///
/// <list type="bullet">
///   <item><c>acquire</c> — try to atomically create the lock; emits a UUID lock token on success.</item>
///   <item><c>release</c> — remove the lock if the supplied token matches; uses a tombstone rename to avoid deleting a replacement lock under a race.</item>
///   <item><c>force-release</c> — operator escape hatch; unconditionally removes the lock.</item>
///   <item><c>status</c> — read-only snapshot of the lock state.</item>
/// </list>
///
/// <para><b>Routing convention.</b> Acquire and status always exit 0 even
/// when the lock is held / stale / unreadable — workflows route on the JSON
/// payload. Token-based release exits non-zero on token mismatch or unreadable
/// lock (programmer error). Force-release always exits 0.</para>
///
/// <para><b>TTL is diagnostic.</b> Expired locks block acquire with
/// <c>reason: stale</c>. They are NEVER auto-deleted. The operator must
/// invoke <c>polyphony lock force-release</c> to clear them per ADR Rev 4.</para>
/// </summary>
[VerbGroup("lock")]
public sealed class LockCommands(
    RunLockStore store,
    RunLockPathResolver pathResolver)
{
    /// <summary>
    /// Try to atomically acquire the run lock for <paramref name="rootId"/>.
    /// On success emits a UUID <c>lock_token</c> the holder must present
    /// to <c>release</c>.
    /// </summary>
    /// <param name="rootId">Root work item id (positive).</param>
    /// <param name="ttlHours">TTL after which the lock is considered stale (default 24).</param>
    /// <param name="by">Optional acquirer name. Defaults to env <c>USERNAME</c>/<c>USER</c>.</param>
    /// <param name="path">Override lock path (default: resolved via git top-level).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("acquire")]
    [VerbResult(typeof(AcquireLockResult))]
    public async Task<int> Acquire(
        int rootId = RequiredInput.MissingInt,
        int ttlHours = 24,
        string by = "",
        string path = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("lock acquire",
            ("--root-id", rootId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (rootId <= 0)
        {
            Emit(new AcquireLockResult
            {
                Path = path,
                Acquired = false,
                Error = "rootId must be positive",
            });
            return ExitCodes.ConfigError;
        }

        if (ttlHours <= 0)
        {
            Emit(new AcquireLockResult
            {
                Path = path,
                Acquired = false,
                Error = "ttlHours must be positive",
            });
            return ExitCodes.ConfigError;
        }

        var resolvedPath = string.IsNullOrEmpty(path)
            ? await pathResolver.ResolveAsync(rootId, ct).ConfigureAwait(false)
            : path;

        var repoRoot = await pathResolver.ResolveRepoRootAsync(ct).ConfigureAwait(false);
        var nowUtc = DateTime.UtcNow;
        var candidate = new RunLock
        {
            Schema = 1,
            RootId = rootId,
            AcquiredAt = nowUtc,
            AcquiredBy = ResolveBy(by),
            Host = Environment.MachineName,
            Pid = Environment.ProcessId,
            LockToken = Guid.NewGuid().ToString("D"),
            TtlUntil = nowUtc.AddHours(ttlHours),
            RepoRoot = repoRoot,
            PolyphonyVersion = ResolveVersion(),
            CommandLine = TryGetCommandLine(),
        };

        var outcome = store.TryAcquire(resolvedPath, candidate, nowUtc);
        if (outcome.Acquired)
        {
            Emit(new AcquireLockResult
            {
                Path = resolvedPath,
                Acquired = true,
                LockToken = candidate.LockToken,
                Lock = candidate,
            });
            return ExitCodes.Success;
        }

        var reason = outcome.Reason!.Value;
        Emit(new AcquireLockResult
        {
            Path = resolvedPath,
            Acquired = false,
            Reason = ReasonName(reason),
            Hint = ReasonHint(reason),
            ExistingLock = outcome.Lock,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Token-based release. Removes the lock when the supplied
    /// <paramref name="lockToken"/> matches the one stored in the file.
    /// Uses a rename-to-tombstone so a stale releaser cannot delete a
    /// replacement lock acquired by a different process between read
    /// and unlink.
    /// </summary>
    /// <param name="rootId">Root work item id whose lock is being released. Required to default the path.</param>
    /// <param name="lockToken">The UUID returned by the matching acquire call.</param>
    /// <param name="path">Override lock path (default: resolved via git top-level).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("release")]
    [VerbResult(typeof(ReleaseLockResult))]
    public async Task<int> Release(
        int rootId = RequiredInput.MissingInt,
        string lockToken = "",
        string path = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("lock release",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--lock-token", string.IsNullOrEmpty(lockToken))) is { } halt)
            return halt;

        if (rootId <= 0)
        {
            Emit(new ReleaseLockResult { Path = path, Released = false, Error = "rootId must be positive" });
            return ExitCodes.ConfigError;
        }

        if (string.IsNullOrEmpty(lockToken))
        {
            Emit(new ReleaseLockResult { Path = path, Released = false, Error = "lockToken is required" });
            return ExitCodes.ConfigError;
        }

        var resolvedPath = string.IsNullOrEmpty(path)
            ? await pathResolver.ResolveAsync(rootId, ct).ConfigureAwait(false)
            : path;

        var outcome = store.TryRelease(resolvedPath, lockToken);
        if (outcome.Released)
        {
            Emit(new ReleaseLockResult
            {
                Path = resolvedPath,
                Released = true,
                ExistingLock = outcome.ExistingLock,
            });
            return ExitCodes.Success;
        }

        var reason = outcome.Reason!.Value;
        Emit(new ReleaseLockResult
        {
            Path = resolvedPath,
            Released = false,
            Reason = ReleaseReasonName(reason),
            ExistingLock = outcome.ExistingLock,
        });

        return reason switch
        {
            ReleaseFailureReason.NotHeld => ExitCodes.Success,
            ReleaseFailureReason.TokenMismatch => ExitCodes.RoutingFailure,
            ReleaseFailureReason.Unreadable => ExitCodes.ConfigError,
            _ => ExitCodes.RoutingFailure,
        };
    }

    /// <summary>
    /// Operator escape hatch. Unconditionally removes the lock for
    /// <paramref name="rootId"/>. Reports whether a lock was present
    /// before removal and a snapshot of its contents (best-effort —
    /// even malformed locks are recorded for diagnostic context).
    /// Always exits 0.
    /// </summary>
    /// <param name="rootId">Root work item id whose lock is being force-released. Required to default the path.</param>
    /// <param name="path">Override lock path (default: resolved via git top-level).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("force-release")]
    [VerbResult(typeof(ForceReleaseLockResult))]
    public async Task<int> ForceRelease(
        int rootId = RequiredInput.MissingInt,
        string path = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("lock force-release",
            ("--root-id", rootId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (rootId <= 0)
        {
            Emit(new ForceReleaseLockResult
            {
                Path = path,
                Released = false,
                WasHeld = false,
                Error = "rootId must be positive",
            });
            return ExitCodes.ConfigError;
        }

        var resolvedPath = string.IsNullOrEmpty(path)
            ? await pathResolver.ResolveAsync(rootId, ct).ConfigureAwait(false)
            : path;

        var outcome = store.ForceRelease(resolvedPath);
        Emit(new ForceReleaseLockResult
        {
            Path = resolvedPath,
            Released = outcome.Released,
            WasHeld = outcome.WasHeld,
            ExistingLock = outcome.ExistingLock,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Read-only inspection of the lock for <paramref name="rootId"/>.
    /// Emits whether the lock exists, parses cleanly, has gone stale,
    /// and how many seconds remain on its TTL when live.
    /// </summary>
    /// <param name="rootId">Root work item id whose lock is being inspected. Required to default the path.</param>
    /// <param name="path">Override lock path (default: resolved via git top-level).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("status")]
    [VerbResult(typeof(LockStatusResult))]
    public async Task<int> Status(
        int rootId = RequiredInput.MissingInt,
        string path = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("lock status",
            ("--root-id", rootId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (rootId <= 0)
        {
            Emit(new LockStatusResult
            {
                Path = path,
                Exists = false,
                Valid = false,
                Stale = false,
                Error = "rootId must be positive",
            });
            return ExitCodes.ConfigError;
        }

        var resolvedPath = string.IsNullOrEmpty(path)
            ? await pathResolver.ResolveAsync(rootId, ct).ConfigureAwait(false)
            : path;

        var outcome = store.Status(resolvedPath, DateTime.UtcNow);
        Emit(new LockStatusResult
        {
            Path = resolvedPath,
            Exists = outcome.Exists,
            Valid = outcome.Valid,
            Stale = outcome.Stale,
            Lock = outcome.Lock,
            TtlRemainingSeconds = outcome.TtlRemainingSeconds,
            ParseError = outcome.ParseError,
        });
        return ExitCodes.Success;
    }

    private static string ResolveBy(string explicitBy)
    {
        if (!string.IsNullOrWhiteSpace(explicitBy))
        {
            return explicitBy;
        }

        var envUsername = Environment.GetEnvironmentVariable("USERNAME");
        if (!string.IsNullOrWhiteSpace(envUsername))
        {
            return envUsername;
        }

        var envUser = Environment.GetEnvironmentVariable("USER");
        if (!string.IsNullOrWhiteSpace(envUser))
        {
            return envUser;
        }

        return Environment.UserName;
    }

    private static string ResolveVersion()
    {
        var asm = typeof(LockCommands).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return info?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static string TryGetCommandLine()
    {
        try
        {
            return Environment.CommandLine;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReasonName(AcquireFailureReason reason) => reason switch
    {
        AcquireFailureReason.Held => "held",
        AcquireFailureReason.Stale => "stale",
        AcquireFailureReason.Unreadable => "unreadable",
        _ => "unknown",
    };

    private static string ReasonHint(AcquireFailureReason reason) => reason switch
    {
        AcquireFailureReason.Held => "another run holds the lock; wait or coordinate with the holder",
        AcquireFailureReason.Stale => "ttl expired; verify the holder is gone then run 'polyphony lock force-release'",
        AcquireFailureReason.Unreadable => "lock file is malformed; inspect or run 'polyphony lock force-release'",
        _ => "unknown",
    };

    private static string ReleaseReasonName(ReleaseFailureReason reason) => reason switch
    {
        ReleaseFailureReason.NotHeld => "not_held",
        ReleaseFailureReason.TokenMismatch => "token_mismatch",
        ReleaseFailureReason.Unreadable => "unreadable",
        _ => "unknown",
    };

    private static void Emit(AcquireLockResult r) =>
        Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.AcquireLockResult));

    private static void Emit(ReleaseLockResult r) =>
        Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ReleaseLockResult));

    private static void Emit(ForceReleaseLockResult r) =>
        Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ForceReleaseLockResult));

    private static void Emit(LockStatusResult r) =>
        Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.LockStatusResult));
}
