using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Manifest;

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
/// </summary>
public sealed partial class ManifestCommands
{
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
    public Task<int> Init(
        int rootId,
        string platformProject,
        string path = RunManifestStore.DefaultRelativePath,
        string createdBy = "",
        bool force = false,
        CancellationToken ct = default)
    {
        _ = ct;

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
    public Task<int> RecordRebase(
        string branch,
        string onto,
        string reason,
        string commit,
        string path = RunManifestStore.DefaultRelativePath,
        string at = "",
        CancellationToken ct = default)
    {
        _ = ct;

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
    public Task<int> RecordApproval(
        string gate,
        string approvedBy,
        string path = RunManifestStore.DefaultRelativePath,
        string detail = "",
        string at = "",
        CancellationToken ct = default)
    {
        _ = ct;

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
