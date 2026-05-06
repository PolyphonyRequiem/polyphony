namespace Polyphony.Locking;

/// <summary>
/// On-disk shape for a polyphony run lock at
/// <c>&lt;repoRoot&gt;/.polyphony/locks/run-{root_id}.lock</c>.
/// Schema 1.
///
/// <para>
/// The lock is the file. Acquisition is OS-atomic via
/// <see cref="System.IO.FileMode.CreateNew"/> with
/// <see cref="System.IO.FileShare.None"/>. Release is conditional on
/// <see cref="LockToken"/> matching, performed via rename-to-tombstone
/// before unlink so a stale releaser cannot delete a replacement
/// acquired by a different process. TTL is diagnostic only — expired
/// locks block acquire with <c>reason: stale</c> and require
/// <c>polyphony lock force-release</c> to clear.
/// </para>
///
/// <para>
/// Times are <see cref="System.DateTime"/> (UTC) so YamlDotNet
/// round-trips them as ISO-8601 scalars; <c>DateTimeOffset</c> would
/// expand its property graph on serialization and corrupt the wire
/// shape.
/// </para>
/// </summary>
public sealed record RunLock
{
    public int Schema { get; init; } = 1;
    public int RootId { get; init; }
    public DateTime AcquiredAt { get; init; }
    public string AcquiredBy { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Pid { get; init; }
    public string LockToken { get; init; } = string.Empty;
    public DateTime TtlUntil { get; init; }

    public string RepoRoot { get; init; } = string.Empty;
    public string PolyphonyVersion { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
}
