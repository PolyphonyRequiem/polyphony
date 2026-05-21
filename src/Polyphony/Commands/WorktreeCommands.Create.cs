using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Worktrees;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony worktree create --root N --branch B [--ref R]</c> — create
/// (or attach to) the per-item worktree at
/// <c>{runs_root}/root-{N}/{slug}/</c> for branch <c>B</c>.
///
/// <para><b>What it does (in order):</b></para>
/// <list type="number">
///   <item>Validate <c>--root</c> &gt; 0.</item>
///   <item>Parse <c>--branch</c> via <see cref="BranchSlug.TryParse"/>;
///         reject anything outside the canonical grammar.</item>
///   <item>Refuse <c>feature/{N}</c> branches — those are
///         <see cref="WorktreeCommands.InitRoot"/>'s job.</item>
///   <item>Refuse if parsed root id != <c>--root</c>
///         (<c>branch_root_mismatch</c>) — catches cross-root typos before
///         any git operation.</item>
///   <item>Resolve <c>(runs_root, main_path)</c> from
///         <c>git rev-parse --git-common-dir</c> via
///         <see cref="RunsRootResolver"/>.</item>
///   <item>Compute <c>root_root = {runs_root}/root-{N}</c> and
///         <c>worktree_path = {root_root}/{parsed.Slug}</c>; assert
///         boundary invariants via <see cref="PathBoundary.IsSameOrSubpath"/>.</item>
///   <item><b>Bootstrap dependency:</b> require
///         <c>{root_root}/feature-{N}</c> to be a registered worktree on
///         <c>feature/{N}</c>. If not → <c>root_not_initialized</c>.</item>
///   <item>Run the create-or-attach matrix (path-exists wins over
///         branch-state, identical shape to <see cref="WorktreeCommands.InitRoot"/>).</item>
/// </list>
///
/// <para>The verb does NOT auto-create the root container — operators
/// must run <c>worktree init-root --root N</c> first. This makes the
/// init dependency observable rather than silently elided.</para>
///
/// <para>Always exits 0 (routing-style verb); consumers branch on
/// <see cref="WorktreeCreateResult.Outcome"/> and (when failed)
/// <see cref="WorktreeCreateResult.Reason"/>.</para>
/// </summary>
public sealed partial class WorktreeCommands
{
    /// <summary>
    /// Create (or attach to) the per-item worktree for <paramref name="branch"/>
    /// under the root container.
    /// </summary>
    /// <param name="root">Root root work-item id (positive integer).</param>
    /// <param name="branch">
    /// Branch name in the canonical polyphony grammar (e.g.
    /// <c>impl/3085-3072</c>, <c>plan/3085-9999</c>, <c>mg/3085_pg-foo</c>,
    /// <c>evidence/3085-3072</c>). <c>feature/{N}</c> is not accepted —
    /// use <c>worktree init-root</c> instead.
    /// </param>
    /// <param name="ref">
    /// Git ref to root the new branch from when the local branch does not
    /// exist yet (e.g. <c>main</c>, <c>origin/feature/3085</c>). Required
    /// when creating a new local branch; ignored when the branch already
    /// exists locally.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("create")]
    [VerbResult(typeof(WorktreeCreateResult))]
    public async Task<int> Create(
        int root = RequiredInput.MissingInt,
        string branch = "",
        string @ref = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("worktree create",
            ("--root", root == RequiredInput.MissingInt),
            ("--branch", string.IsNullOrEmpty(branch))) is { } halt)
            return halt;

        var refOrNull = string.IsNullOrEmpty(@ref) ? null : @ref;

        if (root <= 0)
        {
            EmitCreate(
                root, branch: null, slug: null, refSpec: refOrNull,
                rootRoot: null, worktreePath: null,
                outcome: "failed", reason: "invalid_root",
                error: $"--root must be positive (got {root}).");
            return ExitCodes.Success;
        }

        // ── Step 2-3-4: validate branch + root match ──
        if (!BranchSlug.TryParse(branch, out var parsed, out var rejection))
        {
            EmitCreate(
                root, branch: null, slug: null, refSpec: refOrNull,
                rootRoot: null, worktreePath: null,
                outcome: "failed", reason: "invalid_branch",
                error: rejection);
            return ExitCodes.Success;
        }

        if (parsed.Kind == BranchKind.Feature)
        {
            EmitCreate(
                root, parsed.Slug is { } ? branch : null, parsed.Slug, refOrNull,
                rootRoot: null, worktreePath: null,
                outcome: "failed", reason: "unsupported_branch_kind",
                error: $"feature/{{N}} branches are bootstrapped by 'worktree init-root --root {root}', not 'worktree create'.");
            return ExitCodes.Success;
        }

        if (parsed.RootId != root)
        {
            EmitCreate(
                root, branch, parsed.Slug, refOrNull,
                rootRoot: null, worktreePath: null,
                outcome: "failed", reason: "branch_root_mismatch",
                error: $"--branch '{branch}' has root id {parsed.RootId}, but --root is {root}.");
            return ExitCodes.Success;
        }

        // ── Step 5: resolve common-dir + paths ──
        string commonDir;
        try
        {
            var raw = await _git.GetCommonDirAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw))
            {
                EmitCreate(
                    root, branch, parsed.Slug, refOrNull,
                    rootRoot: null, worktreePath: null,
                    outcome: "failed", reason: "common_dir_unavailable",
                    error: "git rev-parse --git-common-dir returned no path; cwd is not inside a git repository.");
                return ExitCodes.Success;
            }
            commonDir = raw;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitCreate(
                root, branch, parsed.Slug, refOrNull,
                rootRoot: null, worktreePath: null,
                outcome: "failed", reason: "common_dir_unavailable",
                error: ex.Message);
            return ExitCodes.Success;
        }

        string runsRoot, mainPath;
        try
        {
            (runsRoot, mainPath) = RunsRootResolver.Resolve(commonDir);
        }
        catch (ArgumentException ex)
        {
            EmitCreate(
                root, branch, parsed.Slug, refOrNull,
                rootRoot: null, worktreePath: null,
                outcome: "failed", reason: "common_dir_unavailable",
                error: $"Could not derive runs-root from common-dir '{commonDir}': {ex.Message}");
            return ExitCodes.Success;
        }

        var rootRoot = Path.Combine(runsRoot, $"root-{root}");
        var worktreePath = Path.Combine(rootRoot, parsed.Slug);
        var featureWorktreePath = Path.Combine(rootRoot, $"feature-{root}");
        var featureBranch = $"feature/{root}";

        // ── Step 6: defensive boundary invariants ──
        if (!PathBoundary.IsSameOrSubpath(runsRoot, worktreePath))
        {
            EmitCreate(
                root, branch, parsed.Slug, refOrNull,
                rootRoot, worktreePath,
                outcome: "failed", reason: "filesystem_failure",
                error: $"Derived worktree path '{worktreePath}' is not inside runs-root '{runsRoot}'. Refusing.");
            return ExitCodes.Success;
        }
        if (PathBoundary.IsSameOrSubpath(mainPath, worktreePath))
        {
            EmitCreate(
                root, branch, parsed.Slug, refOrNull,
                rootRoot, worktreePath,
                outcome: "failed", reason: "filesystem_failure",
                error: $"Derived worktree path '{worktreePath}' is inside the main worktree '{mainPath}'. Refusing to hijack.");
            return ExitCodes.Success;
        }

        // ── Step 7-8: list, verify init, then matrix ──
        return await RunCreateMatrixAsync(
            root, branch, parsed.Slug, refOrNull,
            rootRoot, worktreePath, featureWorktreePath, featureBranch,
            ct).ConfigureAwait(false);
    }

    private async Task<int> RunCreateMatrixAsync(
        int root,
        string branch,
        string slug,
        string? refSpec,
        string rootRoot,
        string worktreePath,
        string featureWorktreePath,
        string featureBranch,
        CancellationToken ct)
    {
        // Single source of truth for: (a) is the root feature worktree
        // initialized, (b) is the target path already a worktree, and
        // (c) is the local branch checked out elsewhere.
        var listResult = await _git.WorktreeListAsync(ct).ConfigureAwait(false);
        if (!listResult.Succeeded)
        {
            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "failed", reason: "git_failure",
                error: GitStderrOrFallback(listResult, "git worktree list"));
            return ExitCodes.Success;
        }

        IReadOnlyList<WorktreeEntry> entries;
        try
        {
            entries = ParsePorcelain(listResult.Stdout);
        }
        catch (FormatException ex)
        {
            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "failed", reason: "git_failure",
                error: $"Could not parse 'git worktree list --porcelain' output: {ex.Message}");
            return ExitCodes.Success;
        }

        // Bootstrap dependency: the root feature worktree must already
        // exist on feature/{N}. Catches both "root_root missing entirely"
        // and "init-root partially failed" without our needing extra
        // filesystem probes.
        var featureEntry = FindByPath(entries, featureWorktreePath);
        if (featureEntry is null
            || !string.Equals(featureEntry.Branch, featureBranch, StringComparison.Ordinal))
        {
            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "failed", reason: "root_not_initialized",
                error: $"Root feature worktree '{featureWorktreePath}' is not registered on '{featureBranch}'. Run 'polyphony worktree init-root --root {root}' first.");
            return ExitCodes.Success;
        }

        // Path-exists wins over branch-state.
        var existing = FindByPath(entries, worktreePath);
        if (existing is not null)
        {
            if (string.Equals(existing.Branch, branch, StringComparison.Ordinal))
            {
                EmitCreate(
                    root, branch, slug, refSpec, rootRoot, worktreePath,
                    outcome: "idempotent", reason: null, error: null);
                return ExitCodes.Success;
            }

            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "failed", reason: "path_exists_wrong_branch",
                error: $"Worktree at '{worktreePath}' is on branch '{existing.Branch ?? "(detached)"}', not '{branch}'.");
            return ExitCodes.Success;
        }

        if (PathExists(worktreePath))
        {
            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "failed", reason: "path_exists_not_worktree",
                error: $"Path '{worktreePath}' exists but is not a registered git worktree.");
            return ExitCodes.Success;
        }

        // Branch-state matrix.
        string? branchSha;
        try
        {
            branchSha = await _git.RevParseLocalBranchAsync(branch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "failed", reason: "git_failure",
                error: $"git rev-parse --verify refs/heads/{branch} failed: {ex.Message}");
            return ExitCodes.Success;
        }

        if (branchSha is null)
        {
            // Local branch missing. Per the workflow-rerun safety rule
            // (PR 1b3 rubber-duck #3): if origin already has the branch,
            // refuse — operator must fetch + branch first to avoid silent
            // forks.
            IReadOnlyList<string> remotes;
            try
            {
                remotes = await _git.ListRemoteBranchesAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                EmitCreate(
                    root, branch, slug, refSpec, rootRoot, worktreePath,
                    outcome: "failed", reason: "git_failure",
                    error: $"git branch -r failed: {ex.Message}");
                return ExitCodes.Success;
            }

            if (remotes.Any(r => string.Equals(r, branch, StringComparison.Ordinal)))
            {
                EmitCreate(
                    root, branch, slug, refSpec, rootRoot, worktreePath,
                    outcome: "failed", reason: "remote_branch_exists",
                    error: $"Local branch '{branch}' is missing but origin/{branch} exists. " +
                           "Refusing to fork from an arbitrary ref; fetch and create the local branch first.");
                return ExitCodes.Success;
            }

            if (refSpec is null)
            {
                EmitCreate(
                    root, branch, slug, refSpec, rootRoot, worktreePath,
                    outcome: "failed", reason: "ref_required",
                    error: $"Local branch '{branch}' does not exist and --ref was not supplied. Pass --ref <base> to root the new branch.");
                return ExitCodes.Success;
            }

            return await CreateBranchThenMaybeIdempotentAsync(
                root, branch, slug, refSpec, rootRoot, worktreePath, ct).ConfigureAwait(false);
        }

        // Local branch exists. Is it already checked out elsewhere?
        var holder = entries.FirstOrDefault(e =>
            e.Branch is not null && string.Equals(e.Branch, branch, StringComparison.Ordinal));
        if (holder is not null)
        {
            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "failed", reason: "branch_in_use",
                error: $"Branch '{branch}' is already checked out at '{holder.Path}'.");
            return ExitCodes.Success;
        }

        return await AttachBranchThenMaybeIdempotentAsync(
            root, branch, slug, refSpec, rootRoot, worktreePath, ct).ConfigureAwait(false);
    }

    private async Task<int> CreateBranchThenMaybeIdempotentAsync(
        int root, string branch, string slug, string? refSpec,
        string rootRoot, string worktreePath, CancellationToken ct)
    {
        var addResult = await _git.WorktreeAddAsync(branch, worktreePath, refSpec!, ct).ConfigureAwait(false);
        if (addResult.Succeeded)
        {
            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "created", reason: null, error: null);
            return ExitCodes.Success;
        }

        if (await ProbeIdempotentAsync(worktreePath, branch, ct).ConfigureAwait(false))
        {
            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "idempotent", reason: null, error: null);
            return ExitCodes.Success;
        }

        EmitCreate(
            root, branch, slug, refSpec, rootRoot, worktreePath,
            outcome: "failed", reason: "git_failure",
            error: GitStderrOrFallback(addResult, $"git worktree add -b {branch}"));
        return ExitCodes.Success;
    }

    private async Task<int> AttachBranchThenMaybeIdempotentAsync(
        int root, string branch, string slug, string? refSpec,
        string rootRoot, string worktreePath, CancellationToken ct)
    {
        var attachResult = await _git.WorktreeAddAttachAsync(branch, worktreePath, ct).ConfigureAwait(false);
        if (attachResult.Succeeded)
        {
            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "attached", reason: null, error: null);
            return ExitCodes.Success;
        }

        if (await ProbeIdempotentAsync(worktreePath, branch, ct).ConfigureAwait(false))
        {
            EmitCreate(
                root, branch, slug, refSpec, rootRoot, worktreePath,
                outcome: "idempotent", reason: null, error: null);
            return ExitCodes.Success;
        }

        EmitCreate(
            root, branch, slug, refSpec, rootRoot, worktreePath,
            outcome: "failed", reason: "git_failure",
            error: GitStderrOrFallback(attachResult, $"git worktree add {worktreePath} {branch}"));
        return ExitCodes.Success;
    }

    private static void EmitCreate(
        int root,
        string? branch,
        string? slug,
        string? refSpec,
        string? rootRoot,
        string? worktreePath,
        string outcome,
        string? reason,
        string? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new WorktreeCreateResult
            {
                RootId = root,
                Branch = branch,
                Slug = slug,
                Ref = refSpec,
                RootRoot = rootRoot,
                WorktreePath = worktreePath,
                Outcome = outcome,
                Reason = reason,
                Error = error,
            },
            PolyphonyJsonContext.Default.WorktreeCreateResult));
    }
}
