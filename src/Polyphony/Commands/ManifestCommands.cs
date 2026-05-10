using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Paths;
using Polyphony.Manifest;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony manifest</c> command namespace — create, read, hash, and
/// append to the run manifest.
///
/// <para>Path resolution (Rev 4.2): every verb takes <c>--root-id</c>
/// (optional on read/hash/record-* legacy callers; required for
/// derivation) and <c>--path</c>. The resolution matrix:</para>
/// <list type="bullet">
///   <item><description><c>--path</c> non-empty → use it verbatim
///   (<c>path_source = "explicit"</c>). Used by tests and any caller
///   driving an out-of-tree manifest file.</description></item>
///   <item><description>Empty <c>--path</c> + <c>--root-id N</c> →
///   derive via <see cref="PolyphonyStatePaths.GetManifestPathAsync"/>
///   (<c>path_source = "derived"</c>). The Rev 4.2 default that keeps
///   manifests out of the worktree (under <c>&lt;git-common-dir&gt;/polyphony/&lt;rootId&gt;/run.yaml</c>).</description></item>
///   <item><description>Empty <c>--path</c> + missing <c>--root-id</c>
///   → fall back to <c>.polyphony/run.yaml</c>
///   (<c>path_source = "default_legacy"</c>). Transitional only;
///   workflow callers will be updated in Stage 8 to always pass
///   <c>--root-id</c>, at which point this branch becomes dead code.</description></item>
/// </list>
///
/// <para>When <c>--root-id</c> is supplied, mutators AND readers
/// validate <c>manifest.root_id == --root-id</c> after load and refuse
/// to proceed on mismatch (<c>error_code = manifest_root_mismatch</c>).
/// This is the AB#3067 carry-over guard.</para>
///
/// <para>Mutating verbs use the atomic write semantics in
/// <see cref="RunManifestStore"/>; the manifest's <c>topology_hash</c>
/// is recomputed on every save. The manifest is local-only (Rev 4.2);
/// no commit-and-push verb exists — concurrent runs against the same
/// root are serialized by the run lock at
/// <c>&lt;git-common-dir&gt;/polyphony/&lt;rootId&gt;/locks/run.lock</c>.</para>
///
/// <para><see cref="PolyphonyStatePaths"/> backs the path-derivation
/// branch shared by every verb.</para>
/// </summary>
[VerbGroup("manifest")]
public sealed partial class ManifestCommands(PolyphonyStatePaths statePaths)
{
    private readonly PolyphonyStatePaths statePaths = statePaths;

    // ── Path resolution helpers (Rev 4.2) ──────────────────────────────

    /// <summary>
    /// The result of resolving a manifest path from <c>--root-id</c> +
    /// <c>--path</c>. On success, <see cref="Path"/> and
    /// <see cref="Source"/> are set and <see cref="ErrorCode"/> /
    /// <see cref="Error"/> are null. On failure, <see cref="ErrorCode"/>
    /// is set and the verb should emit a structured error and return
    /// <see cref="ExitCodes.ConfigError"/>.
    /// </summary>
    private readonly record struct PathResolution(
        string Path,
        string Source,
        string? ErrorCode,
        string? Error);

    /// <summary>
    /// Implements the <c>--root-id</c> + <c>--path</c> decision matrix
    /// described in the class doc-comment. Never throws — git/path
    /// resolution failures are converted to a populated
    /// <see cref="PathResolution.ErrorCode"/>.
    /// </summary>
    private async Task<PathResolution> ResolveManifestPathAsync(
        int rootId,
        string explicitPath,
        CancellationToken ct)
    {
        var hasRootId = rootId != RequiredInput.MissingInt;

        // Reject explicitly-supplied non-positive root ids before we look
        // at anything else. Don't collapse "caller passed --root-id 0" into
        // the legacy fallback — that would silently re-introduce the
        // AB#3067 bug shape.
        if (hasRootId && rootId <= 0)
        {
            return new PathResolution(
                Path: string.Empty,
                Source: "default_legacy",
                ErrorCode: "invalid_root_id",
                Error: $"--root-id must be positive (got {rootId}).");
        }

        // Explicit path always wins (no git shell-out needed).
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return new PathResolution(explicitPath, "explicit", ErrorCode: null, Error: null);
        }

        // Derived: positive --root-id, no --path.
        if (hasRootId)
        {
            try
            {
                var derived = await this.statePaths.GetManifestPathAsync(rootId, ct);
                return new PathResolution(derived, "derived", ErrorCode: null, Error: null);
            }
            catch (InvalidOperationException ex)
            {
                return new PathResolution(
                    Path: string.Empty,
                    Source: "derived",
                    ErrorCode: "manifest_path_resolution_failed",
                    Error: ex.Message);
            }
        }

        // Neither --root-id nor --path. Stage-8 cleanup will eliminate
        // workflow callers that hit this branch.
        return new PathResolution(
            RunManifestStore.DefaultRelativePath,
            "default_legacy",
            ErrorCode: null,
            Error: null);
    }

    /// <summary>
    /// Returns true when no root-id was supplied OR the loaded manifest's
    /// root matches. Returns false + <paramref name="error"/> when the
    /// caller passed <c>--root-id</c> and it disagrees with what was
    /// loaded — the AB#3067 carry-over case.
    /// </summary>
    private static bool ValidateManifestRoot(RunManifest manifest, int rootId, out string error)
    {
        if (rootId == RequiredInput.MissingInt)
        {
            error = string.Empty;
            return true;
        }

        if (manifest.RootId == rootId)
        {
            error = string.Empty;
            return true;
        }

        error =
            $"manifest root_id ({manifest.RootId}) does not match requested --root-id ({rootId}); " +
            "the worktree may be carrying a stale manifest from a previous run (AB#3067 shape).";
        return false;
    }

    /// <summary>
    /// Creates a fresh run manifest with the supplied <paramref name="rootId"/>,
    /// <paramref name="platformProject"/>, and (optionally) <paramref name="createdBy"/>.
    /// Refuses to overwrite an existing file unless <paramref name="force"/>.
    /// </summary>
    /// <param name="rootId">Run's apex (focus) work-item id (positive). REQUIRED.</param>
    /// <param name="platformProject">Platform-qualified project (e.g. <c>dev.azure.com/org/project</c>).</param>
    /// <param name="path">Optional explicit path. When empty, derived from <paramref name="rootId"/> via <see cref="PolyphonyStatePaths"/>.</param>
    /// <param name="createdBy">Who initiated the run. Defaults to <c>git config user.name</c> or <c>$USERNAME</c>.</param>
    /// <param name="force">When true, overwrites an existing manifest file.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("init")]
    [VerbResult(typeof(ManifestInitResult))]
    public async Task<int> Init(
        int rootId = RequiredInput.MissingInt,
        string platformProject = "",
        string path = "",
        string createdBy = "",
        bool force = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("manifest init",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--platform-project", string.IsNullOrEmpty(platformProject))) is { } halt)
            return halt;

        if (rootId <= 0)
        {
            EmitInitError(path, "default_legacy", "invalid_root_id", rootId, platformProject, createdBy,
                $"rootId must be positive (got {rootId}).");
            return ExitCodes.ConfigError;
        }

        if (string.IsNullOrWhiteSpace(platformProject))
        {
            EmitInitError(path, "default_legacy", errorCode: null, rootId, platformProject, createdBy,
                "platform-project must be non-empty.");
            return ExitCodes.ConfigError;
        }

        var resolution = await ResolveManifestPathAsync(rootId, path, ct);
        if (resolution.ErrorCode is not null)
        {
            EmitInitError(resolution.Path, resolution.Source, resolution.ErrorCode,
                rootId, platformProject, createdBy, resolution.Error ?? "path resolution failed.");
            return ExitCodes.ConfigError;
        }

        var resolvedPath = resolution.Path;
        var resolvedCreatedBy = ResolveCreatedBy(createdBy);
        var existed = File.Exists(resolvedPath);
        if (existed && !force)
        {
            EmitInitError(resolvedPath, resolution.Source, errorCode: null,
                rootId, platformProject, resolvedCreatedBy,
                $"manifest already exists at '{resolvedPath}' (pass --force to overwrite).");
            return ExitCodes.ConfigError;
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
        RunManifestStore.Save(resolvedPath, manifest);

        Emit(new ManifestInitResult
        {
            Path = resolvedPath,
            RootId = rootId,
            PlatformProject = platformProject,
            Created = !existed,
            CreatedBy = resolvedCreatedBy,
            TopologyHash = manifest.TopologyHash,
            Message = existed ? $"overwrote existing manifest (--force)" : null,
            PathSource = resolution.Source,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Loads the manifest, validates invariants, recomputes the topology
    /// hash, and emits the full manifest plus the freshly-computed hash
    /// and a match flag.
    /// </summary>
    /// <param name="rootId">When supplied, validates the loaded manifest's <c>root_id</c> and derives the path if <paramref name="path"/> is empty.</param>
    /// <param name="path">Optional explicit path; see class doc for resolution rules.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("read")]
    [VerbResult(typeof(ManifestReadResult))]
    public async Task<int> Read(
        int rootId = RequiredInput.MissingInt,
        string path = "",
        CancellationToken ct = default)
    {
        var resolution = await ResolveManifestPathAsync(rootId, path, ct);
        if (resolution.ErrorCode is not null)
        {
            EmitReadError(resolution.Path, resolution.Source, resolution.ErrorCode, resolution.Error!);
            return ExitCodes.ConfigError;
        }

        var resolvedPath = resolution.Path;

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(resolvedPath);

            if (!ValidateManifestRoot(manifest, rootId, out var mismatchError))
            {
                EmitReadError(resolvedPath, resolution.Source, "manifest_root_mismatch", mismatchError);
                return ExitCodes.ConfigError;
            }

            var computed = TopologyHasher.ComputeHash(manifest.MergeGroups);
            Emit(new ManifestReadResult
            {
                Path = resolvedPath,
                Manifest = manifest,
                ComputedTopologyHash = computed,
                TopologyHashMatches = string.Equals(computed, manifest.TopologyHash, StringComparison.Ordinal),
                PathSource = resolution.Source,
            });
            return ExitCodes.Success;
        }
        catch (FileNotFoundException ex)
        {
            EmitReadError(resolvedPath, resolution.Source, "manifest_not_found", ex.Message);
            return ExitCodes.CacheError;
        }
        catch (InvalidOperationException ex)
        {
            EmitReadError(resolvedPath, resolution.Source, "manifest_malformed", ex.Message);
            return ExitCodes.ConfigError;
        }
    }

    /// <summary>
    /// Recomputes the topology hash from the current
    /// <c>merge_groups</c> and reports whether it matches the stored value.
    /// </summary>
    /// <param name="rootId">When supplied, validates the loaded manifest's <c>root_id</c> and derives the path if <paramref name="path"/> is empty.</param>
    /// <param name="path">Optional explicit path; see class doc for resolution rules.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("topology-hash")]
    [VerbResult(typeof(ManifestTopologyHashResult))]
    public async Task<int> TopologyHash(
        int rootId = RequiredInput.MissingInt,
        string path = "",
        CancellationToken ct = default)
    {
        var resolution = await ResolveManifestPathAsync(rootId, path, ct);
        if (resolution.ErrorCode is not null)
        {
            EmitHashError(resolution.Path, resolution.Source, resolution.ErrorCode, resolution.Error!);
            return ExitCodes.ConfigError;
        }

        var resolvedPath = resolution.Path;

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(resolvedPath);

            if (!ValidateManifestRoot(manifest, rootId, out var mismatchError))
            {
                EmitHashError(resolvedPath, resolution.Source, "manifest_root_mismatch", mismatchError);
                return ExitCodes.ConfigError;
            }

            var computed = TopologyHasher.ComputeHash(manifest.MergeGroups);
            Emit(new ManifestTopologyHashResult
            {
                Path = resolvedPath,
                TopologyHash = computed,
                StoredTopologyHash = manifest.TopologyHash,
                Matches = string.Equals(computed, manifest.TopologyHash, StringComparison.Ordinal),
                PathSource = resolution.Source,
            });
            return ExitCodes.Success;
        }
        catch (FileNotFoundException ex)
        {
            EmitHashError(resolvedPath, resolution.Source, "manifest_not_found", ex.Message);
            return ExitCodes.CacheError;
        }
        catch (InvalidOperationException ex)
        {
            EmitHashError(resolvedPath, resolution.Source, "manifest_malformed", ex.Message);
            return ExitCodes.ConfigError;
        }
    }

    /// <summary>
    /// Appends a rebase record to the manifest. Append-only by convention.
    /// </summary>
    /// <param name="branch">Branch that was rebased (e.g. <c>mg/1234_data-layer</c>).</param>
    /// <param name="onto">Branch the rebase landed onto (e.g. <c>feature/1234</c>).</param>
    /// <param name="reason">Categorical reason: <c>cross_mg_code_dep</c>, <c>child_plan_drift</c>, or <c>manual</c>.</param>
    /// <param name="commit">New HEAD commit after the rebase.</param>
    /// <param name="rootId">When supplied, validates the loaded manifest's <c>root_id</c> and derives the path if <paramref name="path"/> is empty.</param>
    /// <param name="path">Optional explicit path; see class doc for resolution rules.</param>
    /// <param name="at">Optional ISO-8601 timestamp; defaults to now (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("record-rebase")]
    [VerbResult(typeof(ManifestRebaseRecordResult))]
    public async Task<int> RecordRebase(
        string branch = "",
        string onto = "",
        string reason = "",
        string commit = "",
        int rootId = RequiredInput.MissingInt,
        string path = "",
        string at = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("manifest record-rebase",
            ("--branch", string.IsNullOrEmpty(branch)),
            ("--onto", string.IsNullOrEmpty(onto)),
            ("--reason", string.IsNullOrEmpty(reason)),
            ("--commit", string.IsNullOrEmpty(commit))) is { } halt)
            return halt;

        if (string.IsNullOrWhiteSpace(branch) ||
            string.IsNullOrWhiteSpace(onto) ||
            string.IsNullOrWhiteSpace(reason) ||
            string.IsNullOrWhiteSpace(commit))
        {
            EmitRebaseError(path, "default_legacy", errorCode: null,
                branch, onto, reason, commit, "branch, onto, reason, and commit must all be non-empty.");
            return ExitCodes.ConfigError;
        }

        if (!TryParseTimestamp(at, out var recordedAt, out var parseError))
        {
            EmitRebaseError(path, "default_legacy", errorCode: null,
                branch, onto, reason, commit, parseError);
            return ExitCodes.ConfigError;
        }

        var resolution = await ResolveManifestPathAsync(rootId, path, ct);
        if (resolution.ErrorCode is not null)
        {
            EmitRebaseError(resolution.Path, resolution.Source, resolution.ErrorCode,
                branch, onto, reason, commit, resolution.Error!);
            return ExitCodes.ConfigError;
        }

        var resolvedPath = resolution.Path;

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(resolvedPath);

            if (!ValidateManifestRoot(manifest, rootId, out var mismatchError))
            {
                EmitRebaseError(resolvedPath, resolution.Source, "manifest_root_mismatch",
                    branch, onto, reason, commit, mismatchError);
                return ExitCodes.ConfigError;
            }

            var record = new RebaseRecord
            {
                Branch = branch,
                Onto = onto,
                Reason = reason,
                Commit = commit,
                RecordedAt = recordedAt,
            };
            manifest.Rebases.Add(record);
            RunManifestStore.Save(resolvedPath, manifest);

            Emit(new ManifestRebaseRecordResult
            {
                Path = resolvedPath,
                RebaseCount = manifest.Rebases.Count,
                Branch = branch,
                Onto = onto,
                Reason = reason,
                Commit = commit,
                RecordedAt = recordedAt,
                PathSource = resolution.Source,
            });
            return ExitCodes.Success;
        }
        catch (FileNotFoundException ex)
        {
            EmitRebaseError(resolvedPath, resolution.Source, "manifest_not_found",
                branch, onto, reason, commit, ex.Message);
            return ExitCodes.CacheError;
        }
        catch (InvalidOperationException ex)
        {
            EmitRebaseError(resolvedPath, resolution.Source, "manifest_malformed",
                branch, onto, reason, commit, ex.Message);
            return ExitCodes.ConfigError;
        }
    }

    /// <summary>
    /// Appends a human-approval record to the manifest. Append-only by convention.
    /// </summary>
    /// <param name="gate">The named gate that was approved.</param>
    /// <param name="approvedBy">The approver's display name.</param>
    /// <param name="rootId">When supplied, validates the loaded manifest's <c>root_id</c> and derives the path if <paramref name="path"/> is empty.</param>
    /// <param name="path">Optional explicit path; see class doc for resolution rules.</param>
    /// <param name="detail">Optional free-form detail describing what was approved.</param>
    /// <param name="at">Optional ISO-8601 timestamp; defaults to now (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("record-approval")]
    [VerbResult(typeof(ManifestApprovalRecordResult))]
    public async Task<int> RecordApproval(
        string gate = "",
        string approvedBy = "",
        int rootId = RequiredInput.MissingInt,
        string path = "",
        string detail = "",
        string at = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("manifest record-approval",
            ("--gate", string.IsNullOrEmpty(gate)),
            ("--approved-by", string.IsNullOrEmpty(approvedBy))) is { } halt)
            return halt;

        if (string.IsNullOrWhiteSpace(gate) || string.IsNullOrWhiteSpace(approvedBy))
        {
            EmitApprovalError(path, "default_legacy", errorCode: null,
                gate, approvedBy, detail, "gate and approved-by must both be non-empty.");
            return ExitCodes.ConfigError;
        }

        if (!TryParseTimestamp(at, out var approvedAt, out var parseError))
        {
            EmitApprovalError(path, "default_legacy", errorCode: null,
                gate, approvedBy, detail, parseError);
            return ExitCodes.ConfigError;
        }

        var resolution = await ResolveManifestPathAsync(rootId, path, ct);
        if (resolution.ErrorCode is not null)
        {
            EmitApprovalError(resolution.Path, resolution.Source, resolution.ErrorCode,
                gate, approvedBy, detail, resolution.Error!);
            return ExitCodes.ConfigError;
        }

        var resolvedPath = resolution.Path;

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(resolvedPath);

            if (!ValidateManifestRoot(manifest, rootId, out var mismatchError))
            {
                EmitApprovalError(resolvedPath, resolution.Source, "manifest_root_mismatch",
                    gate, approvedBy, detail, mismatchError);
                return ExitCodes.ConfigError;
            }

            var record = new HumanApprovalRecord
            {
                Gate = gate,
                ApprovedBy = approvedBy,
                ApprovedAt = approvedAt,
                Detail = string.IsNullOrEmpty(detail) ? null : detail,
            };
            manifest.HumanApprovals.Add(record);
            RunManifestStore.Save(resolvedPath, manifest);

            Emit(new ManifestApprovalRecordResult
            {
                Path = resolvedPath,
                ApprovalCount = manifest.HumanApprovals.Count,
                Gate = gate,
                ApprovedBy = approvedBy,
                ApprovedAt = approvedAt,
                Detail = record.Detail,
                PathSource = resolution.Source,
            });
            return ExitCodes.Success;
        }
        catch (FileNotFoundException ex)
        {
            EmitApprovalError(resolvedPath, resolution.Source, "manifest_not_found",
                gate, approvedBy, detail, ex.Message);
            return ExitCodes.CacheError;
        }
        catch (InvalidOperationException ex)
        {
            EmitApprovalError(resolvedPath, resolution.Source, "manifest_malformed",
                gate, approvedBy, detail, ex.Message);
            return ExitCodes.ConfigError;
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
    /// <param name="rootId">When supplied, validates the loaded manifest's <c>root_id</c> and derives the path if <paramref name="path"/> is empty.</param>
    /// <param name="path">Optional explicit path; see class doc for resolution rules.</param>
    /// <param name="prNumber">PR number whose merge is being recorded. Required when <paramref name="mergeCommit"/> is supplied; when both are omitted, the legacy unconditional-bump path is taken.</param>
    /// <param name="mergeCommit">Platform-reported merge commit SHA. Required when <paramref name="prNumber"/> is supplied.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("record-plan-merge")]
    [VerbResult(typeof(ManifestRecordPlanMergeResult))]
    public async Task<int> RecordPlanMerge(
        string item = "",
        int rootId = RequiredInput.MissingInt,
        string path = "",
        int prNumber = 0,
        string mergeCommit = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("manifest record-plan-merge",
            ("--item", string.IsNullOrEmpty(item))) is { } halt)
            return halt;

        if (!TryNormalizePlanKey(item, out var itemKey, out var keyError))
        {
            EmitRecordPlanMergeError(path, "default_legacy", errorCode: null,
                item, prNumber, mergeCommit, keyError);
            return ExitCodes.ConfigError;
        }

        var hasPrIdentity = prNumber > 0 || !string.IsNullOrEmpty(mergeCommit);
        if (hasPrIdentity)
        {
            if (prNumber <= 0)
            {
                EmitRecordPlanMergeError(path, "default_legacy", errorCode: null,
                    item, prNumber, mergeCommit,
                    "--pr-number must be positive when --merge-commit is supplied.");
                return ExitCodes.ConfigError;
            }

            if (string.IsNullOrWhiteSpace(mergeCommit))
            {
                EmitRecordPlanMergeError(path, "default_legacy", errorCode: null,
                    item, prNumber, mergeCommit,
                    "--merge-commit must be non-empty when --pr-number is supplied.");
                return ExitCodes.ConfigError;
            }
        }

        var resolution = await ResolveManifestPathAsync(rootId, path, ct);
        if (resolution.ErrorCode is not null)
        {
            EmitRecordPlanMergeError(resolution.Path, resolution.Source, resolution.ErrorCode,
                item, prNumber, mergeCommit, resolution.Error!);
            return ExitCodes.ConfigError;
        }

        var resolvedPath = resolution.Path;

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(resolvedPath);

            if (!ValidateManifestRoot(manifest, rootId, out var mismatchError))
            {
                EmitRecordPlanMergeError(resolvedPath, resolution.Source, "manifest_root_mismatch",
                    item, prNumber, mergeCommit, mismatchError);
                return ExitCodes.ConfigError;
            }

            if (prNumber > 0)
            {
                // Ledger mode: delegate the idempotency / conflict /
                // record decision to the shared ManifestPlanLedger so this
                // verb and `polyphony pr merge-plan-pr` enforce identical
                // semantics.
                var outcome = ManifestPlanLedger.Apply(manifest, itemKey, prNumber, mergeCommit, DateTime.UtcNow);

                if (outcome.ConflictReason is not null)
                {
                    EmitRecordPlanMergeError(resolvedPath, resolution.Source, errorCode: null,
                        item, prNumber, mergeCommit, outcome.ConflictReason);
                    return ExitCodes.ConfigError;
                }

                if (outcome.Recorded)
                {
                    RunManifestStore.Save(resolvedPath, manifest);
                }

                Emit(new ManifestRecordPlanMergeResult
                {
                    Path = resolvedPath,
                    ItemKey = itemKey,
                    PreviousGeneration = outcome.PreviousGeneration,
                    CurrentGeneration = outcome.CurrentGeneration,
                    Recorded = outcome.Recorded,
                    PrNumber = prNumber,
                    MergeCommit = mergeCommit,
                    PathSource = resolution.Source,
                });
                return ExitCodes.Success;
            }

            // Legacy mode (no PR identity supplied): unconditional bump,
            // no ledger entry. Preserved so existing callers keep working
            // until they switch to ledger mode.
            var previous = manifest.PlanGenerations.TryGetValue(itemKey, out var existingGen) ? existingGen : 0;
            var current = previous + 1;
            manifest.PlanGenerations[itemKey] = current;

            RunManifestStore.Save(resolvedPath, manifest);

            Emit(new ManifestRecordPlanMergeResult
            {
                Path = resolvedPath,
                ItemKey = itemKey,
                PreviousGeneration = previous,
                CurrentGeneration = current,
                Recorded = true,
                PrNumber = prNumber,
                MergeCommit = mergeCommit,
                PathSource = resolution.Source,
            });
            return ExitCodes.Success;
        }
        catch (FileNotFoundException ex)
        {
            EmitRecordPlanMergeError(resolvedPath, resolution.Source, "manifest_not_found",
                item, prNumber, mergeCommit, ex.Message);
            return ExitCodes.CacheError;
        }
        catch (InvalidOperationException ex)
        {
            EmitRecordPlanMergeError(resolvedPath, resolution.Source, "manifest_malformed",
                item, prNumber, mergeCommit, ex.Message);
            return ExitCodes.ConfigError;
        }
    }

    /// <summary>
    /// Reads <see cref="RunManifest.PlanGenerations"/> for the given plan
    /// key. Returns <c>0</c> with <c>present = false</c> when the key has
    /// no recorded generation yet (i.e. no plan has been merged for this
    /// item) — generations start at 0 by convention.
    /// </summary>
    /// <param name="item">Plan key: <c>root</c> or a positive numeric item id.</param>
    /// <param name="rootId">When supplied, validates the loaded manifest's <c>root_id</c> and derives the path if <paramref name="path"/> is empty.</param>
    /// <param name="path">Optional explicit path; see class doc for resolution rules.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("read-plan-generation")]
    [VerbResult(typeof(ManifestReadPlanGenerationResult))]
    public async Task<int> ReadPlanGeneration(
        string item = "",
        int rootId = RequiredInput.MissingInt,
        string path = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("manifest read-plan-generation",
            ("--item", string.IsNullOrEmpty(item))) is { } halt)
            return halt;

        if (!TryNormalizePlanKey(item, out var itemKey, out var keyError))
        {
            EmitReadPlanGenerationError(path, "default_legacy", errorCode: null, item, keyError);
            return ExitCodes.ConfigError;
        }

        var resolution = await ResolveManifestPathAsync(rootId, path, ct);
        if (resolution.ErrorCode is not null)
        {
            EmitReadPlanGenerationError(resolution.Path, resolution.Source, resolution.ErrorCode,
                item, resolution.Error!);
            return ExitCodes.ConfigError;
        }

        var resolvedPath = resolution.Path;

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(resolvedPath);

            if (!ValidateManifestRoot(manifest, rootId, out var mismatchError))
            {
                EmitReadPlanGenerationError(resolvedPath, resolution.Source, "manifest_root_mismatch",
                    item, mismatchError);
                return ExitCodes.ConfigError;
            }

            var present = manifest.PlanGenerations.TryGetValue(itemKey, out var generation);
            Emit(new ManifestReadPlanGenerationResult
            {
                Path = resolvedPath,
                ItemKey = itemKey,
                Generation = present ? generation : 0,
                Present = present,
                PathSource = resolution.Source,
            });
            return ExitCodes.Success;
        }
        catch (FileNotFoundException ex)
        {
            EmitReadPlanGenerationError(resolvedPath, resolution.Source, "manifest_not_found",
                item, ex.Message);
            return ExitCodes.CacheError;
        }
        catch (InvalidOperationException ex)
        {
            EmitReadPlanGenerationError(resolvedPath, resolution.Source, "manifest_malformed",
                item, ex.Message);
            return ExitCodes.ConfigError;
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
    /// <param name="rootId">When supplied, validates the loaded manifest's <c>root_id</c> and derives the path if <paramref name="path"/> is empty.</param>
    /// <param name="path">Optional explicit path; see class doc for resolution rules.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("read-plan-generation-snapshot")]
    [VerbResult(typeof(ManifestReadPlanGenerationSnapshotResult))]
    public async Task<int> ReadPlanGenerationSnapshot(
        string item = "",
        string ancestorIds = "",
        int rootId = RequiredInput.MissingInt,
        string path = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("manifest read-plan-generation-snapshot",
            ("--item", string.IsNullOrEmpty(item))) is { } halt)
            return halt;

        if (!TryNormalizePlanKey(item, out var itemKey, out var keyError))
        {
            EmitSnapshotError(path, "default_legacy", errorCode: null, item, ancestorIds, keyError);
            return ExitCodes.ConfigError;
        }

        if (!TryParseAncestorChain(ancestorIds, out var ancestorKeys, out var ancestorError))
        {
            EmitSnapshotError(path, "default_legacy", errorCode: null, item, ancestorIds, ancestorError);
            return ExitCodes.ConfigError;
        }

        // Root-plan invariant: a root plan has no ancestors. Refuse a
        // non-empty chain rather than silently producing an inconsistent
        // snapshot — the caller has bug data and we should fail loud.
        if (string.Equals(itemKey, "root", StringComparison.Ordinal) && ancestorKeys.Count > 0)
        {
            EmitSnapshotError(path, "default_legacy", errorCode: null, item, ancestorIds,
                "root plan must not declare ancestors (got --ancestor-ids with entries).");
            return ExitCodes.ConfigError;
        }

        // The plan key must not appear in its own ancestor chain.
        if (ancestorKeys.Contains(itemKey))
        {
            EmitSnapshotError(path, "default_legacy", errorCode: null, item, ancestorIds,
                $"--item value '{itemKey}' must not appear in --ancestor-ids.");
            return ExitCodes.ConfigError;
        }

        var resolution = await ResolveManifestPathAsync(rootId, path, ct);
        if (resolution.ErrorCode is not null)
        {
            EmitSnapshotError(resolution.Path, resolution.Source, resolution.ErrorCode,
                item, ancestorIds, resolution.Error!);
            return ExitCodes.ConfigError;
        }

        var resolvedPath = resolution.Path;

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(resolvedPath);

            if (!ValidateManifestRoot(manifest, rootId, out var mismatchError))
            {
                EmitSnapshotError(resolvedPath, resolution.Source, "manifest_root_mismatch",
                    item, ancestorIds, mismatchError);
                return ExitCodes.ConfigError;
            }

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
                Path = resolvedPath,
                ItemKey = itemKey,
                ParentItemKey = parentItemKey,
                ParentPlanGeneration = parentGen,
                AncestorPlanGenerations = snapshot,
                PathSource = resolution.Source,
            });
            return ExitCodes.Success;
        }
        catch (FileNotFoundException ex)
        {
            EmitSnapshotError(resolvedPath, resolution.Source, "manifest_not_found",
                item, ancestorIds, ex.Message);
            return ExitCodes.CacheError;
        }
        catch (InvalidOperationException ex)
        {
            EmitSnapshotError(resolvedPath, resolution.Source, "manifest_malformed",
                item, ancestorIds, ex.Message);
            return ExitCodes.ConfigError;
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

    private static void EmitRecordPlanMergeError(
        string path, string? pathSource, string? errorCode,
        string item, int prNumber, string mergeCommit, string error)
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
            PathSource = pathSource,
            ErrorCode = errorCode,
        });

    private static void EmitReadPlanGenerationError(
        string path, string? pathSource, string? errorCode,
        string item, string error)
        => Emit(new ManifestReadPlanGenerationResult
        {
            Path = path,
            ItemKey = item,
            Generation = 0,
            Present = false,
            Error = error,
            PathSource = pathSource,
            ErrorCode = errorCode,
        });

    private static void EmitSnapshotError(
        string path, string? pathSource, string? errorCode,
        string item, string ancestorIds, string error)
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
            PathSource = pathSource,
            ErrorCode = errorCode,
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

    private static void EmitInitError(
        string path, string? pathSource, string? errorCode,
        int rootId, string platformProject, string createdBy, string error)
        => Emit(new ManifestInitResult
        {
            Path = path,
            RootId = rootId,
            PlatformProject = platformProject,
            Created = false,
            CreatedBy = createdBy,
            TopologyHash = string.Empty,
            Error = error,
            PathSource = pathSource,
            ErrorCode = errorCode,
        });

    private static void EmitReadError(
        string path, string? pathSource, string? errorCode, string error)
        => Emit(new ManifestReadResult
        {
            Path = path,
            Manifest = new RunManifest(),
            ComputedTopologyHash = string.Empty,
            TopologyHashMatches = false,
            Error = error,
            PathSource = pathSource,
            ErrorCode = errorCode,
        });

    private static void EmitHashError(
        string path, string? pathSource, string? errorCode, string error)
        => Emit(new ManifestTopologyHashResult
        {
            Path = path,
            TopologyHash = string.Empty,
            StoredTopologyHash = string.Empty,
            Matches = false,
            Error = error,
            PathSource = pathSource,
            ErrorCode = errorCode,
        });

    private static void EmitRebaseError(
        string path, string? pathSource, string? errorCode,
        string branch, string onto, string reason, string commit, string error)
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
            PathSource = pathSource,
            ErrorCode = errorCode,
        });

    private static void EmitApprovalError(
        string path, string? pathSource, string? errorCode,
        string gate, string approvedBy, string detail, string error)
        => Emit(new ManifestApprovalRecordResult
        {
            Path = path,
            ApprovalCount = 0,
            Gate = gate,
            ApprovedBy = approvedBy,
            ApprovedAt = default,
            Detail = string.IsNullOrEmpty(detail) ? null : detail,
            Error = error,
            PathSource = pathSource,
            ErrorCode = errorCode,
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
