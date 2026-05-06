namespace Polyphony.Manifest;

/// <summary>
/// In-memory mutator for <see cref="RunManifest.Rebases"/>. Encapsulates the
/// idempotency rules for "record this rebase event" so every consumer
/// (P9 cascade remedy, manual record-rebase verbs, future rebase-emitting
/// flows) applies the same key and the same allow-list of reasons.
///
/// <para>Pure with respect to disk: mutates the supplied
/// <see cref="RunManifest"/> instance in place when it elects to append,
/// but never reads or writes the underlying YAML. Callers own the
/// load/save lifecycle.</para>
///
/// <para><b>Idempotency key:</b> the triple <c>(branch, commit, reason)</c>.
/// A retry of a partially-completed cascade-remedy run replays the same
/// triple; <see cref="Apply"/> recognises it and returns
/// <see cref="RebaseLedgerOutcome.DuplicateSkipped"/> without mutating —
/// so the caller can safely re-record after a manifest-push race.</para>
///
/// <para><b>Reason allow-list:</b> mirrors the enumerated set documented on
/// <see cref="RebaseRecord"/> and the branch-model ADR — currently
/// <c>cross_mg_code_dep</c>, <c>child_plan_drift</c>, <c>manual</c>. An
/// invalid reason returns <see cref="RebaseLedgerOutcome.InvalidReason"/>
/// with the full allow-list so the caller can render a useful diagnostic
/// rather than falling through to a generic "validation failed".</para>
/// </summary>
public static class ManifestRebaseLedger
{
    /// <summary>
    /// The categorical reasons accepted by <see cref="Apply"/>. Match
    /// the wire form used in <see cref="RebaseRecord.Reason"/>.
    /// </summary>
    public static IReadOnlyList<string> AllowedReasons { get; } = new[]
    {
        "cross_mg_code_dep",
        "child_plan_drift",
        "manual",
    };

    /// <summary>
    /// Apply a rebase-event record to the manifest in memory.
    /// </summary>
    /// <param name="manifest">Loaded manifest. Mutated when the outcome is <see cref="RebaseLedgerOutcome.Appended"/>.</param>
    /// <param name="branch">Branch that was rebased (e.g. <c>plan/100-200</c>). Required, non-empty.</param>
    /// <param name="commit">New HEAD SHA after the rebase. Required, non-empty. <see cref="RebaseRecord.Onto"/> is intentionally NOT part of this surface — this PR ships a minimal ledger sufficient for the cascade remedy and follow-up callers can add an <c>onto</c> overload if they need it.</param>
    /// <param name="reason">One of <see cref="AllowedReasons"/>. Anything else returns <see cref="RebaseLedgerOutcome.InvalidReason"/> without mutation.</param>
    /// <param name="recordedAt">Timestamp written into a fresh ledger entry. Injected for tests.</param>
    public static RebaseLedgerOutcome Apply(
        RunManifest manifest,
        string branch,
        string commit,
        string reason,
        DateTime recordedAt)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(branch);
        ArgumentException.ThrowIfNullOrEmpty(commit);
        ArgumentException.ThrowIfNullOrEmpty(reason);

        if (!AllowedReasons.Contains(reason, StringComparer.Ordinal))
        {
            return new RebaseLedgerOutcome.InvalidReason(reason, AllowedReasons);
        }

        var existing = manifest.Rebases.FirstOrDefault(r =>
            string.Equals(r.Branch, branch, StringComparison.Ordinal)
            && string.Equals(r.Commit, commit, StringComparison.Ordinal)
            && string.Equals(r.Reason, reason, StringComparison.Ordinal));

        if (existing is not null)
        {
            return new RebaseLedgerOutcome.DuplicateSkipped(existing);
        }

        var record = new RebaseRecord
        {
            Branch = branch,
            // The cascade-remedy verb owns the `Onto` value (the parent's
            // refname); this minimal ledger leaves it empty and lets the
            // verb patch it in if desired. Future overload may accept it
            // as a parameter.
            Onto = string.Empty,
            Reason = reason,
            Commit = commit,
            RecordedAt = recordedAt,
        };
        manifest.Rebases.Add(record);
        return new RebaseLedgerOutcome.Appended(record);
    }
}

/// <summary>
/// Outcome of <see cref="ManifestRebaseLedger.Apply"/>. Discriminated union
/// over the three terminal states so callers can route — append-on-fresh,
/// no-op on idempotent replay, validation-error on bad reason — without
/// inspecting nullable fields.
/// </summary>
public abstract record RebaseLedgerOutcome
{
    /// <summary>A fresh entry was appended to <see cref="RunManifest.Rebases"/>. <see cref="Record"/> is the appended record.</summary>
    public sealed record Appended(RebaseRecord Record) : RebaseLedgerOutcome;

    /// <summary>An entry with the same <c>(branch, commit, reason)</c> triple already existed. Manifest unchanged. <see cref="Existing"/> is the prior entry for caller diagnostics.</summary>
    public sealed record DuplicateSkipped(RebaseRecord Existing) : RebaseLedgerOutcome;

    /// <summary>The supplied <c>reason</c> was not in <see cref="ManifestRebaseLedger.AllowedReasons"/>. Manifest unchanged.</summary>
    public sealed record InvalidReason(string Provided, IReadOnlyList<string> Allowed) : RebaseLedgerOutcome;
}
