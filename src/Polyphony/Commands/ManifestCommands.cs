using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Polyphony.Postconditions;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony manifest</c> command namespace — create, read, hash, and
/// append to the run manifest at <c>.polyphony/run.yaml</c>.
///
/// <para>All verbs operate against a path passed via <c>--path</c>
/// (defaults to <c>.polyphony/run.yaml</c>) so they can be unit-tested
/// against temp files. Mutating verbs use the atomic write semantics in
/// <see cref="RunManifestStore"/>; the manifest's <c>topology_hash</c>
/// is recomputed on every save.</para>
///
/// <para><see cref="IGitClient"/> and <see cref="IPostconditionVerifier"/>
/// are injected for the <see cref="CommitAndPush"/> verb only; the
/// read/init/record-* verbs that pre-existed <c>commit-and-push</c> do
/// not consume them.</para>
/// </summary>
[VerbGroup("manifest")]
public sealed partial class ManifestCommands(IGitClient git, IPostconditionVerifier postconditions)
{
    private readonly IGitClient git = git;
    private readonly IPostconditionVerifier postconditions = postconditions;

    /// <summary>
    /// Creates a fresh run manifest at <paramref name="path"/> with the
    /// supplied <paramref name="rootId"/>, <paramref name="platformProject"/>,
    /// and (optionally) <paramref name="createdBy"/>. Refuses to
    /// overwrite an existing file unless <paramref name="force"/>.
    /// </summary>
    /// <param name="rootId">Run's apex (focus) work-item id (positive).</param>
    /// <param name="platformProject">Platform-qualified project (e.g. <c>dev.azure.com/org/project</c>).</param>
    /// <param name="path">Path to the manifest file. Defaults to <c>.polyphony/run.yaml</c>.</param>
    /// <param name="createdBy">Who initiated the run. Defaults to <c>git config user.name</c> or <c>$USERNAME</c>.</param>
    /// <param name="force">When true, overwrites an existing manifest file.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("init")]
    [VerbResult(typeof(ManifestInitResult))]
    public Task<int> Init(
        int rootId = RequiredInput.MissingInt,
        string platformProject = "",
        string path = RunManifestStore.DefaultRelativePath,
        string createdBy = "",
        bool force = false,
        CancellationToken ct = default)
    {
        _ = ct;

        if (RequiredInput.HaltIfMissing("manifest init",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--platform-project", string.IsNullOrEmpty(platformProject))) is { } halt)
            return Task.FromResult(halt);

        if (rootId <= 0)
        {
            EmitInitError(path, rootId, platformProject, createdBy, $"rootId must be positive (got {rootId}).");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        if (string.IsNullOrWhiteSpace(platformProject))
        {
            EmitInitError(path, rootId, platformProject, createdBy, "platform-project must be non-empty.");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        var resolvedCreatedBy = ResolveCreatedBy(createdBy);
        var existed = File.Exists(path);
        if (existed && !force)
        {
            EmitInitError(path, rootId, platformProject, resolvedCreatedBy,
                $"manifest already exists at '{path}' (pass --force to overwrite).");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        var manifest = new RunManifest
        {
            Schema = RunManifestValidator.SupportedSchema,
            RootId = rootId,
            PlatformProject = platformProject,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = resolvedCreatedBy,
            BranchModelVersion = RunManifestValidator.SupportedBranchModelVersion,
        };

        // Save recomputes topology hash from the empty MG list.
        RunManifestStore.Save(path, manifest);

        Emit(new ManifestInitResult
        {
            Path = path,
            RootId = rootId,
            PlatformProject = platformProject,
            Created = !existed,
            CreatedBy = resolvedCreatedBy,
            TopologyHash = manifest.TopologyHash,
            Message = existed ? $"overwrote existing manifest (--force)" : null,
        });
        return Task.FromResult(ExitCodes.Success);
    }

    /// <summary>
    /// Loads the manifest from <paramref name="path"/>, validates
    /// invariants, recomputes the topology hash, and emits the full
    /// manifest plus the freshly-computed hash and a match flag.
    /// </summary>
    /// <param name="path">Path to the manifest file.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("read")]
    [VerbResult(typeof(ManifestReadResult))]
    public Task<int> Read(
        string path = RunManifestStore.DefaultRelativePath,
        CancellationToken ct = default)
    {
        _ = ct;

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(path);
            var computed = TopologyHasher.ComputeHash(manifest.MergeGroups);
            Emit(new ManifestReadResult
            {
                Path = path,
                Manifest = manifest,
                ComputedTopologyHash = computed,
                TopologyHashMatches = string.Equals(computed, manifest.TopologyHash, StringComparison.Ordinal),
            });
            return Task.FromResult(ExitCodes.Success);
        }
        catch (FileNotFoundException ex)
        {
            EmitReadError(path, ex.Message);
            return Task.FromResult(ExitCodes.CacheError);
        }
        catch (InvalidOperationException ex)
        {
            EmitReadError(path, ex.Message);
            return Task.FromResult(ExitCodes.ConfigError);
        }
    }

    /// <summary>
    /// Recomputes the topology hash from the current
    /// <c>merge_groups</c> in <paramref name="path"/> and reports
    /// whether it matches the stored value.
    /// </summary>
    /// <param name="path">Path to the manifest file.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("topology-hash")]
    [VerbResult(typeof(ManifestTopologyHashResult))]
    public Task<int> TopologyHash(
        string path = RunManifestStore.DefaultRelativePath,
        CancellationToken ct = default)
    {
        _ = ct;

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(path);
            var computed = TopologyHasher.ComputeHash(manifest.MergeGroups);
            Emit(new ManifestTopologyHashResult
            {
                Path = path,
                TopologyHash = computed,
                StoredTopologyHash = manifest.TopologyHash,
                Matches = string.Equals(computed, manifest.TopologyHash, StringComparison.Ordinal),
            });
            return Task.FromResult(ExitCodes.Success);
        }
        catch (FileNotFoundException ex)
        {
            EmitHashError(path, ex.Message);
            return Task.FromResult(ExitCodes.CacheError);
        }
        catch (InvalidOperationException ex)
        {
            EmitHashError(path, ex.Message);
            return Task.FromResult(ExitCodes.ConfigError);
        }
    }

    /// <summary>
    /// Appends a rebase record to the manifest at <paramref name="path"/>.
    /// Append-only by convention.
    /// </summary>
    /// <param name="branch">Branch that was rebased (e.g. <c>mg/1234_data-layer</c>).</param>
    /// <param name="onto">Branch the rebase landed onto (e.g. <c>feature/1234</c>).</param>
    /// <param name="reason">Categorical reason: <c>cross_mg_code_dep</c>, <c>child_plan_drift</c>, or <c>manual</c>.</param>
    /// <param name="commit">New HEAD commit after the rebase.</param>
    /// <param name="path">Path to the manifest file.</param>
    /// <param name="at">Optional ISO-8601 timestamp; defaults to now (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("record-rebase")]
    [VerbResult(typeof(ManifestRebaseRecordResult))]
    public Task<int> RecordRebase(
        string branch = "",
        string onto = "",
        string reason = "",
        string commit = "",
        string path = RunManifestStore.DefaultRelativePath,
        string at = "",
        CancellationToken ct = default)
    {
        _ = ct;

        if (RequiredInput.HaltIfMissing("manifest record-rebase",
            ("--branch", string.IsNullOrEmpty(branch)),
            ("--onto", string.IsNullOrEmpty(onto)),
            ("--reason", string.IsNullOrEmpty(reason)),
            ("--commit", string.IsNullOrEmpty(commit))) is { } halt)
            return Task.FromResult(halt);

        if (string.IsNullOrWhiteSpace(branch) ||
            string.IsNullOrWhiteSpace(onto) ||
            string.IsNullOrWhiteSpace(reason) ||
            string.IsNullOrWhiteSpace(commit))
        {
            EmitRebaseError(path, branch, onto, reason, commit, "branch, onto, reason, and commit must all be non-empty.");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        if (!TryParseTimestamp(at, out var recordedAt, out var parseError))
        {
            EmitRebaseError(path, branch, onto, reason, commit, parseError);
            return Task.FromResult(ExitCodes.ConfigError);
        }

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(path);
            var record = new RebaseRecord
            {
                Branch = branch,
                Onto = onto,
                Reason = reason,
                Commit = commit,
                RecordedAt = recordedAt,
            };
            manifest.Rebases.Add(record);
            RunManifestStore.Save(path, manifest);

            Emit(new ManifestRebaseRecordResult
            {
                Path = path,
                RebaseCount = manifest.Rebases.Count,
                Branch = branch,
                Onto = onto,
                Reason = reason,
                Commit = commit,
                RecordedAt = recordedAt,
            });
            return Task.FromResult(ExitCodes.Success);
        }
        catch (FileNotFoundException ex)
        {
            EmitRebaseError(path, branch, onto, reason, commit, ex.Message);
            return Task.FromResult(ExitCodes.CacheError);
        }
        catch (InvalidOperationException ex)
        {
            EmitRebaseError(path, branch, onto, reason, commit, ex.Message);
            return Task.FromResult(ExitCodes.ConfigError);
        }
    }

    /// <summary>
    /// Appends a human-approval record to the manifest at <paramref name="path"/>.
    /// Append-only by convention.
    /// </summary>
    /// <param name="gate">The named gate that was approved.</param>
    /// <param name="approvedBy">The approver's display name.</param>
    /// <param name="path">Path to the manifest file.</param>
    /// <param name="detail">Optional free-form detail describing what was approved.</param>
    /// <param name="at">Optional ISO-8601 timestamp; defaults to now (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("record-approval")]
    [VerbResult(typeof(ManifestApprovalRecordResult))]
    public Task<int> RecordApproval(
        string gate = "",
        string approvedBy = "",
        string path = RunManifestStore.DefaultRelativePath,
        string detail = "",
        string at = "",
        CancellationToken ct = default)
    {
        _ = ct;

        if (RequiredInput.HaltIfMissing("manifest record-approval",
            ("--gate", string.IsNullOrEmpty(gate)),
            ("--approved-by", string.IsNullOrEmpty(approvedBy))) is { } halt)
            return Task.FromResult(halt);

        if (string.IsNullOrWhiteSpace(gate) || string.IsNullOrWhiteSpace(approvedBy))
        {
            EmitApprovalError(path, gate, approvedBy, detail, "gate and approved-by must both be non-empty.");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        if (!TryParseTimestamp(at, out var approvedAt, out var parseError))
        {
            EmitApprovalError(path, gate, approvedBy, detail, parseError);
            return Task.FromResult(ExitCodes.ConfigError);
        }

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(path);
            var record = new HumanApprovalRecord
            {
                Gate = gate,
                ApprovedBy = approvedBy,
                ApprovedAt = approvedAt,
                Detail = string.IsNullOrEmpty(detail) ? null : detail,
            };
            manifest.HumanApprovals.Add(record);
            RunManifestStore.Save(path, manifest);

            Emit(new ManifestApprovalRecordResult
            {
                Path = path,
                ApprovalCount = manifest.HumanApprovals.Count,
                Gate = gate,
                ApprovedBy = approvedBy,
                ApprovedAt = approvedAt,
                Detail = record.Detail,
            });
            return Task.FromResult(ExitCodes.Success);
        }
        catch (FileNotFoundException ex)
        {
            EmitApprovalError(path, gate, approvedBy, detail, ex.Message);
            return Task.FromResult(ExitCodes.CacheError);
        }
        catch (InvalidOperationException ex)
        {
            EmitApprovalError(path, gate, approvedBy, detail, ex.Message);
            return Task.FromResult(ExitCodes.ConfigError);
        }
    }

    /// <summary>
    /// Bumps <see cref="RunManifest.PlanGenerations"/> for the given plan
    /// key by 1 (creating the entry if missing). The key is either the
    /// literal <c>"root"</c> (root plan) or a positive numeric work-item
    /// id (descendant plan), matching the convention used elsewhere in
    /// the manifest.
    ///
    /// <para>Idempotency: when both <paramref name="prNumber"/> and
    /// <paramref name="mergeCommit"/> are supplied, the verb consults
    /// the <see cref="RunManifest.MergedPlanPrs"/> ledger before
    /// mutating. Three outcomes:</para>
    /// <list type="number">
    ///   <item><description>No prior entry for the PR — bump generation,
    ///   append a new ledger entry, return <c>recorded=true</c>.</description></item>
    ///   <item><description>Existing entry matches (same item key + same
    ///   merge commit) — return <c>recorded=false</c>; no mutation.</description></item>
    ///   <item><description>Existing entry conflicts (different item key
    ///   or different merge commit) — error with <see cref="ExitCodes.ConfigError"/>.</description></item>
    /// </list>
    /// <para>Legacy callers may omit <paramref name="prNumber"/> entirely
    /// (default <c>0</c>); the verb then bumps unconditionally and writes
    /// no ledger entry. This path is preserved for transitional use only;
    /// the production <c>polyphony pr merge-plan-pr</c> verb MUST pass
    /// the PR identity.</para>
    ///
    /// <para>Concurrency: the verb performs a best-effort atomic
    /// load-mutate-save against the manifest file via
    /// <see cref="RunManifestStore"/>. The caller is responsible for
    /// holding the run lock for the run-cluster (typically via
    /// <c>polyphony lock acquire</c>) when racing manifest mutations from
    /// other processes. Without the lock, last-writer-wins.</para>
    /// </summary>
    /// <param name="item">Plan key: <c>root</c> or a positive numeric item id.</param>
    /// <param name="path">Path to the manifest file.</param>
    /// <param name="prNumber">PR number whose merge is being recorded. Required when <paramref name="mergeCommit"/> is supplied; when both are omitted, the legacy unconditional-bump path is taken.</param>
    /// <param name="mergeCommit">Platform-reported merge commit SHA. Required when <paramref name="prNumber"/> is supplied.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("record-plan-merge")]
    [VerbResult(typeof(ManifestRecordPlanMergeResult))]
    public Task<int> RecordPlanMerge(
        string item = "",
        string path = RunManifestStore.DefaultRelativePath,
        int prNumber = 0,
        string mergeCommit = "",
        CancellationToken ct = default)
    {
        _ = ct;

        if (RequiredInput.HaltIfMissing("manifest record-plan-merge",
            ("--item", string.IsNullOrEmpty(item))) is { } halt)
            return Task.FromResult(halt);

        if (!TryNormalizePlanKey(item, out var itemKey, out var keyError))
        {
            EmitRecordPlanMergeError(path, item, prNumber, mergeCommit, keyError);
            return Task.FromResult(ExitCodes.ConfigError);
        }

        var hasPrIdentity = prNumber > 0 || !string.IsNullOrEmpty(mergeCommit);
        if (hasPrIdentity)
        {
            if (prNumber <= 0)
            {
                EmitRecordPlanMergeError(path, item, prNumber, mergeCommit,
                    "--pr-number must be positive when --merge-commit is supplied.");
                return Task.FromResult(ExitCodes.ConfigError);
            }

            if (string.IsNullOrWhiteSpace(mergeCommit))
            {
                EmitRecordPlanMergeError(path, item, prNumber, mergeCommit,
                    "--merge-commit must be non-empty when --pr-number is supplied.");
                return Task.FromResult(ExitCodes.ConfigError);
            }
        }

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(path);

            if (prNumber > 0)
            {
                // Ledger mode: delegate the idempotency / conflict /
                // record decision to the shared ManifestPlanLedger so this
                // verb and `polyphony pr merge-plan-pr` enforce identical
                // semantics.
                var outcome = ManifestPlanLedger.Apply(manifest, itemKey, prNumber, mergeCommit, DateTime.UtcNow);

                if (outcome.ConflictReason is not null)
                {
                    EmitRecordPlanMergeError(path, item, prNumber, mergeCommit, outcome.ConflictReason);
                    return Task.FromResult(ExitCodes.ConfigError);
                }

                if (outcome.Recorded)
                {
                    RunManifestStore.Save(path, manifest);
                }

                Emit(new ManifestRecordPlanMergeResult
                {
                    Path = path,
                    ItemKey = itemKey,
                    PreviousGeneration = outcome.PreviousGeneration,
                    CurrentGeneration = outcome.CurrentGeneration,
                    Recorded = outcome.Recorded,
                    PrNumber = prNumber,
                    MergeCommit = mergeCommit,
                });
                return Task.FromResult(ExitCodes.Success);
            }

            // Legacy mode (no PR identity supplied): unconditional bump,
            // no ledger entry. Preserved so existing callers keep working
            // until they switch to ledger mode.
            var previous = manifest.PlanGenerations.TryGetValue(itemKey, out var existingGen) ? existingGen : 0;
            var current = previous + 1;
            manifest.PlanGenerations[itemKey] = current;

            RunManifestStore.Save(path, manifest);

            Emit(new ManifestRecordPlanMergeResult
            {
                Path = path,
                ItemKey = itemKey,
                PreviousGeneration = previous,
                CurrentGeneration = current,
                Recorded = true,
                PrNumber = prNumber,
                MergeCommit = mergeCommit,
            });
            return Task.FromResult(ExitCodes.Success);
        }
        catch (FileNotFoundException ex)
        {
            EmitRecordPlanMergeError(path, item, prNumber, mergeCommit, ex.Message);
            return Task.FromResult(ExitCodes.CacheError);
        }
        catch (InvalidOperationException ex)
        {
            EmitRecordPlanMergeError(path, item, prNumber, mergeCommit, ex.Message);
            return Task.FromResult(ExitCodes.ConfigError);
        }
    }

    /// <summary>
    /// Reads <see cref="RunManifest.PlanGenerations"/> for the given plan
    /// key. Returns <c>0</c> with <c>present = false</c> when the key has
    /// no recorded generation yet (i.e. no plan has been merged for this
    /// item) — generations start at 0 by convention.
    /// </summary>
    /// <param name="item">Plan key: <c>root</c> or a positive numeric item id.</param>
    /// <param name="path">Path to the manifest file.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("read-plan-generation")]
    [VerbResult(typeof(ManifestReadPlanGenerationResult))]
    public Task<int> ReadPlanGeneration(
        string item = "",
        string path = RunManifestStore.DefaultRelativePath,
        CancellationToken ct = default)
    {
        _ = ct;

        if (RequiredInput.HaltIfMissing("manifest read-plan-generation",
            ("--item", string.IsNullOrEmpty(item))) is { } halt)
            return Task.FromResult(halt);

        if (!TryNormalizePlanKey(item, out var itemKey, out var keyError))
        {
            EmitReadPlanGenerationError(path, item, keyError);
            return Task.FromResult(ExitCodes.ConfigError);
        }

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(path);
            var present = manifest.PlanGenerations.TryGetValue(itemKey, out var generation);
            Emit(new ManifestReadPlanGenerationResult
            {
                Path = path,
                ItemKey = itemKey,
                Generation = present ? generation : 0,
                Present = present,
            });
            return Task.FromResult(ExitCodes.Success);
        }
        catch (FileNotFoundException ex)
        {
            EmitReadPlanGenerationError(path, item, ex.Message);
            return Task.FromResult(ExitCodes.CacheError);
        }
        catch (InvalidOperationException ex)
        {
            EmitReadPlanGenerationError(path, item, ex.Message);
            return Task.FromResult(ExitCodes.ConfigError);
        }
    }

    /// <summary>
    /// Computes a plan-generation snapshot for a plan that is about to be
    /// opened, recording the current generation of every declared
    /// ancestor. The result is intended to be embedded in the plan PR's
    /// body front-matter so that staleness can be detected later.
    ///
    /// <para>For the root plan, pass <c>--item root</c> and omit
    /// <c>--ancestor-ids</c>. The result is an empty snapshot.</para>
    ///
    /// <para>For a descendant plan, pass the work-item id as
    /// <c>--item</c> and the comma-separated ancestor chain as
    /// <c>--ancestor-ids</c> in <em>immediate-parent-first</em> order
    /// (e.g. <c>--ancestor-ids "1234,root"</c> for a grandchild whose
    /// immediate parent is item 1234 and whose grandparent is the root
    /// plan). The verb is purely projection: it does NOT consult the
    /// work-item tree — the caller is expected to derive the chain via
    /// twig or another hierarchy walker.</para>
    /// </summary>
    /// <param name="item">Plan key: <c>root</c> or a positive numeric item id.</param>
    /// <param name="ancestorIds">Comma-separated ancestor chain (immediate parent first). Empty for the root plan.</param>
    /// <param name="path">Path to the manifest file.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("read-plan-generation-snapshot")]
    [VerbResult(typeof(ManifestReadPlanGenerationSnapshotResult))]
    public Task<int> ReadPlanGenerationSnapshot(
        string item = "",
        string ancestorIds = "",
        string path = RunManifestStore.DefaultRelativePath,
        CancellationToken ct = default)
    {
        _ = ct;

        if (RequiredInput.HaltIfMissing("manifest read-plan-generation-snapshot",
            ("--item", string.IsNullOrEmpty(item))) is { } halt)
            return Task.FromResult(halt);

        if (!TryNormalizePlanKey(item, out var itemKey, out var keyError))
        {
            EmitSnapshotError(path, item, ancestorIds, keyError);
            return Task.FromResult(ExitCodes.ConfigError);
        }

        if (!TryParseAncestorChain(ancestorIds, out var ancestorKeys, out var ancestorError))
        {
            EmitSnapshotError(path, item, ancestorIds, ancestorError);
            return Task.FromResult(ExitCodes.ConfigError);
        }

        // Root-plan invariant: a root plan has no ancestors. Refuse a
        // non-empty chain rather than silently producing an inconsistent
        // snapshot — the caller has bug data and we should fail loud.
        if (string.Equals(itemKey, "root", StringComparison.Ordinal) && ancestorKeys.Count > 0)
        {
            EmitSnapshotError(path, item, ancestorIds,
                "root plan must not declare ancestors (got --ancestor-ids with entries).");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        // The plan key must not appear in its own ancestor chain.
        if (ancestorKeys.Contains(itemKey))
        {
            EmitSnapshotError(path, item, ancestorIds,
                $"--item value '{itemKey}' must not appear in --ancestor-ids.");
            return Task.FromResult(ExitCodes.ConfigError);
        }

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(path);

            string? parentItemKey = ancestorKeys.Count == 0 ? null : ancestorKeys[0];
            var parentGen = parentItemKey is null
                ? 0
                : (manifest.PlanGenerations.TryGetValue(parentItemKey, out var pg) ? pg : 0);

            var snapshot = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var key in ancestorKeys)
            {
                snapshot[key] = manifest.PlanGenerations.TryGetValue(key, out var g) ? g : 0;
            }

            Emit(new ManifestReadPlanGenerationSnapshotResult
            {
                Path = path,
                ItemKey = itemKey,
                ParentItemKey = parentItemKey,
                ParentPlanGeneration = parentGen,
                AncestorPlanGenerations = snapshot,
            });
            return Task.FromResult(ExitCodes.Success);
        }
        catch (FileNotFoundException ex)
        {
            EmitSnapshotError(path, item, ancestorIds, ex.Message);
            return Task.FromResult(ExitCodes.CacheError);
        }
        catch (InvalidOperationException ex)
        {
            EmitSnapshotError(path, item, ancestorIds, ex.Message);
            return Task.FromResult(ExitCodes.ConfigError);
        }
    }

    /// <summary>
    /// Validates and normalizes a plan key (the <c>--item</c> arg). Returns
    /// the canonical form: <c>"root"</c> for the root plan or the decimal
    /// representation of a positive integer for descendants.
    /// </summary>
    private static bool TryNormalizePlanKey(string raw, out string itemKey, out string error)
    {
        itemKey = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "--item must be 'root' or a positive numeric work-item id.";
            return false;
        }

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, "root", StringComparison.Ordinal))
        {
            itemKey = "root";
            return true;
        }

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asInt) || asInt <= 0)
        {
            error = $"--item value '{raw}' must be 'root' or a positive numeric work-item id.";
            return false;
        }

        itemKey = asInt.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Parses the <c>--ancestor-ids</c> argument: a comma-separated list
    /// where each entry is either <c>"root"</c> or a positive numeric id.
    /// Empty input parses to an empty list. Duplicates are rejected so the
    /// snapshot map is unambiguous.
    /// </summary>
    private static bool TryParseAncestorChain(string raw, out List<string> ancestorKeys, out string error)
    {
        ancestorKeys = new List<string>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            if (!TryNormalizePlanKey(entry, out var key, out var perEntryError))
            {
                error = $"--ancestor-ids entry '{entry}' is invalid: {perEntryError}";
                return false;
            }

            if (!seen.Add(key))
            {
                error = $"--ancestor-ids contains duplicate entry '{key}'.";
                return false;
            }

            ancestorKeys.Add(key);
        }

        return true;
    }

    private static void EmitRecordPlanMergeError(string path, string item, int prNumber, string mergeCommit, string error)
        => Emit(new ManifestRecordPlanMergeResult
        {
            Path = path,
            ItemKey = item,
            PreviousGeneration = 0,
            CurrentGeneration = 0,
            Recorded = false,
            PrNumber = prNumber,
            MergeCommit = mergeCommit,
            Error = error,
        });

    private static void EmitReadPlanGenerationError(string path, string item, string error)
        => Emit(new ManifestReadPlanGenerationResult
        {
            Path = path,
            ItemKey = item,
            Generation = 0,
            Present = false,
            Error = error,
        });

    private static void EmitSnapshotError(string path, string item, string ancestorIds, string error)
    {
        _ = ancestorIds; // surfaced via Error message; kept for signature parity
        Emit(new ManifestReadPlanGenerationSnapshotResult
        {
            Path = path,
            ItemKey = item,
            ParentItemKey = null,
            ParentPlanGeneration = 0,
            AncestorPlanGenerations = new Dictionary<string, int>(StringComparer.Ordinal),
            Error = error,
        });
    }

    private static void Emit(ManifestRecordPlanMergeResult r)
        => Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ManifestRecordPlanMergeResult));

    private static void Emit(ManifestReadPlanGenerationResult r)
        => Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ManifestReadPlanGenerationResult));

    private static void Emit(ManifestReadPlanGenerationSnapshotResult r)
        => Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ManifestReadPlanGenerationSnapshotResult));

    private static bool TryParseTimestamp(string raw, out DateTime value, out string error)
    {
        if (string.IsNullOrEmpty(raw))
        {
            value = DateTime.UtcNow;
            error = string.Empty;
            return true;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            error = string.Empty;
            return true;
        }

        value = default;
        error = $"--at value '{raw}' is not a valid ISO-8601 timestamp.";
        return false;
    }

    private static string ResolveCreatedBy(string supplied)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            return supplied;
        }

        var fromEnvUser = Environment.GetEnvironmentVariable("USERNAME") ?? Environment.GetEnvironmentVariable("USER");
        if (!string.IsNullOrWhiteSpace(fromEnvUser))
        {
            return fromEnvUser;
        }

        return Environment.UserName;
    }

    private static void EmitInitError(string path, int rootId, string platformProject, string createdBy, string error)
        => Emit(new ManifestInitResult
        {
            Path = path,
            RootId = rootId,
            PlatformProject = platformProject,
            Created = false,
            CreatedBy = createdBy,
            TopologyHash = string.Empty,
            Error = error,
        });

    private static void EmitReadError(string path, string error)
        => Emit(new ManifestReadResult
        {
            Path = path,
            Manifest = new RunManifest(),
            ComputedTopologyHash = string.Empty,
            TopologyHashMatches = false,
            Error = error,
        });

    private static void EmitHashError(string path, string error)
        => Emit(new ManifestTopologyHashResult
        {
            Path = path,
            TopologyHash = string.Empty,
            StoredTopologyHash = string.Empty,
            Matches = false,
            Error = error,
        });

    private static void EmitRebaseError(string path, string branch, string onto, string reason, string commit, string error)
        => Emit(new ManifestRebaseRecordResult
        {
            Path = path,
            RebaseCount = 0,
            Branch = branch,
            Onto = onto,
            Reason = reason,
            Commit = commit,
            RecordedAt = default,
            Error = error,
        });

    private static void EmitApprovalError(string path, string gate, string approvedBy, string detail, string error)
        => Emit(new ManifestApprovalRecordResult
        {
            Path = path,
            ApprovalCount = 0,
            Gate = gate,
            ApprovedBy = approvedBy,
            ApprovedAt = default,
            Detail = string.IsNullOrEmpty(detail) ? null : detail,
            Error = error,
        });

    private static void Emit(ManifestInitResult r)
        => Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ManifestInitResult));

    private static void Emit(ManifestReadResult r)
        => Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ManifestReadResult));

    private static void Emit(ManifestTopologyHashResult r)
        => Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ManifestTopologyHashResult));

    private static void Emit(ManifestRebaseRecordResult r)
        => Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ManifestRebaseRecordResult));

    private static void Emit(ManifestApprovalRecordResult r)
        => Console.WriteLine(JsonSerializer.Serialize(r, PolyphonyJsonContext.Default.ManifestApprovalRecordResult));
}
