using System.Globalization;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;
using Polyphony.Locking;
using Polyphony.Manifest;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Merge a plan PR (head = <c>plan/{root}-{item_id}</c> or
    /// <c>plan/{root}</c>) into its parent plan branch (or the feature
    /// branch for the root plan), then record the merge in the run
    /// manifest's <c>merged_plan_prs</c> ledger and push the manifest
    /// mutation to <c>feature/{root}</c>. The whole sequence runs under
    /// the same-root run lock at
    /// <c>&lt;repoRoot&gt;/.polyphony/locks/run-{rootId}.lock</c>.
    ///
    /// <para><b>Lock-before-merge.</b> Per the Phase 3 P5 rubber-duck
    /// pass, the lock is acquired BEFORE the platform-side merge — not
    /// after — so concurrent verb invocations cannot race the
    /// pre-merge poll against each other. This is the only way the
    /// future P6 stale-generation block can be made safe.</para>
    ///
    /// <para><b>Compound transactional verb.</b> Steps:</para>
    /// <list type="number">
    ///   <item>Validate inputs and derive expected head/base branches.</item>
    ///   <item>Resolve repo slug from <c>git remote get-url origin</c>.</item>
    ///   <item>Acquire the run lock (fail-fast — workflows can route on the lock-held outcome and gate on a human or back off).</item>
    ///   <item>Verify the worktree is clean (refuse otherwise — manifest mutation needs a deterministic checkout).</item>
    ///   <item>Fetch the feature branch (canonical manifest home) and the PR's head branch.</item>
    ///   <item>Poll the PR; validate identity (<c>headRefName</c>, <c>baseRefName</c>); branch on state (<c>OPEN</c> → merge with <c>--match-head-commit</c>; <c>MERGED</c> → reuse merge commit SHA; anything else → refuse).</item>
    ///   <item>Checkout feature branch and reset to <c>origin/feature/{root}</c> so the manifest write lands at the latest tip.</item>
    ///   <item>Apply the merge to the in-memory manifest via the shared <see cref="ManifestPlanLedger"/>; the helper enforces idempotency and conflict semantics identically with <c>polyphony manifest record-plan-merge</c>.</item>
    ///   <item>If the helper appended a fresh entry: save, stage, commit, push. If push is rejected, hard-reset the local checkout and surface <c>manifest_push_rejected</c> for retry.</item>
    ///   <item>Release the lock in <c>finally</c>.</item>
    /// </list>
    ///
    /// <para><b>Idempotency.</b> Re-running the verb after a complete
    /// success is a no-op (PR already merged + ledger has the entry →
    /// reports <c>Merged=true, AlreadyMerged=true, ManifestRecorded=false</c>).
    /// Re-running after a partial success (merged but manifest not pushed)
    /// completes the manifest half (<c>ManifestRecorded=true,
    /// ManifestPushed=true</c>).</para>
    ///
    /// <para><b>Routing-style exit code.</b> The verb always exits 0 on
    /// outcomes the workflow can route on (lock held, push rejected,
    /// identity mismatch, etc.) — consumers branch on
    /// <see cref="PrMergePlanPrResult.ErrorCode"/>. Exits non-zero only
    /// for genuinely unexpected exceptions (with <c>internal_error</c>).</para>
    /// </summary>
    /// <param name="rootId">Run's root work-item id (positive).</param>
    /// <param name="itemId">Plan-owning work-item id; equal to <paramref name="rootId"/> for the root plan.</param>
    /// <param name="prNumber">PR number to merge (positive).</param>
    /// <param name="parentItemId">Immediate plan-tree parent's id; required for descendants of descendants. Omit for root plan and direct children of root plan.</param>
    /// <param name="ancestorIds">Comma-separated ancestor ids ABOVE the immediate parent, ending in the literal token <c>root</c>. Empty when item is root or a direct child of root. Required for the P8b diff-validation guard to detect ancestor-plan touches; when omitted the guard runs in degraded mode (no ancestor check). Format matches the <c>ancestor_ids</c> field emitted by <c>plan derive-ancestor-chain</c> minus the leading parent id.</param>
    /// <param name="manifestPath">Path to the run manifest. Defaults to <c>.polyphony/run.yaml</c>.</param>
    /// <param name="admin">Pass <c>--admin</c> to bypass branch-protection on the platform-side merge.</param>
    /// <param name="lockTtlHours">Run-lock TTL (default 24).</param>
    /// <param name="by">Lock acquirer name; defaults to <c>USERNAME</c>/<c>USER</c> env.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("merge-plan-pr")]
    [VerbResult(typeof(PrMergePlanPrResult))]
    public async Task<int> MergePlanPr(
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        int prNumber = RequiredInput.MissingInt,
        int parentItemId = 0,
        string ancestorIds = "",
        string manifestPath = RunManifestStore.DefaultRelativePath,
        bool admin = false,
        int lockTtlHours = 24,
        string by = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr merge-plan-pr",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt),
            ("--pr-number", prNumber == RequiredInput.MissingInt)) is { } halt)
            return halt;

        // ── 1. Validate inputs + derive head/base. ──────────────────────────
        if (!Branching.RootId.TryParse(rootId, out var root))
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "config_error",
                $"--root-id must be positive (got {rootId})");

        if (!WorkItemId.TryParse(itemId, out var item))
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "config_error",
                $"--item-id must be positive (got {itemId})");

        if (prNumber <= 0)
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "config_error",
                $"--pr-number must be positive (got {prNumber})");

        bool isRootPlan = itemId == rootId;
        string itemKey;
        string headBranch;
        string baseBranch;
        int resolvedParent = 0;

        if (isRootPlan)
        {
            if (parentItemId != 0)
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "config_error",
                    $"--parent-item-id must be omitted when --item-id == --root-id (got {parentItemId}); the root plan has no parent.");
            itemKey = "root";
            headBranch = BranchNameBuilder.RootPlan(root).Value;
            baseBranch = BranchNameBuilder.Feature(root).Value;
        }
        else
        {
            if (parentItemId == 0)
            {
                headBranch = BranchNameBuilder.DescendantPlan(root, item).Value;
                baseBranch = BranchNameBuilder.RootPlan(root).Value;
            }
            else
            {
                if (!WorkItemId.TryParse(parentItemId, out var parentItem))
                    return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "config_error",
                        $"--parent-item-id must be positive (got {parentItemId})");
                if (parentItemId == itemId)
                    return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "config_error",
                        $"--parent-item-id ({parentItemId}) must not equal --item-id; a plan cannot be its own parent.");
                if (parentItemId == rootId)
                    return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "config_error",
                        $"--parent-item-id ({parentItemId}) equals --root-id; omit --parent-item-id when the parent is the root plan.");
                resolvedParent = parentItemId;
                headBranch = BranchNameBuilder.DescendantPlan(root, item).Value;
                baseBranch = BranchNameBuilder.DescendantPlan(root, parentItem).Value;
            }
            itemKey = itemId.ToString(CultureInfo.InvariantCulture);
        }

        var manifestBranch = BranchNameBuilder.Feature(root).Value;

        // ── 2. Resolve repo slug. ──────────────────────────────────────────
        string slug;
        try
        {
            slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitMergePlanError(rootId, itemId, resolvedParent, prNumber, "repo_not_resolved", ex.Message,
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch);
        }
        if (string.IsNullOrEmpty(slug))
            return EmitMergePlanError(rootId, itemId, resolvedParent, prNumber, "repo_not_resolved",
                "Could not resolve repo slug from origin remote",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch);

        // ── 3. Acquire run lock. ───────────────────────────────────────────
        string lockPath;
        try
        {
            lockPath = await lockPathResolver.ResolveAsync(rootId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return EmitMergePlanError(rootId, itemId, resolvedParent, prNumber, "internal_error", ex.Message,
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug);
        }

        var lockToken = Guid.NewGuid().ToString("N");
        var nowUtc = DateTime.UtcNow;
        var candidate = new RunLock
        {
            Schema = 1,
            RootId = rootId,
            LockToken = lockToken,
            AcquiredBy = string.IsNullOrWhiteSpace(by)
                ? Environment.GetEnvironmentVariable("USERNAME") ?? Environment.GetEnvironmentVariable("USER") ?? "unknown"
                : by,
            AcquiredAt = nowUtc,
            TtlUntil = nowUtc.AddHours(lockTtlHours),
            Pid = Environment.ProcessId,
            Host = Environment.MachineName,
            RepoRoot = await SafeResolveRepoRootAsync(ct).ConfigureAwait(false),
        };

        var acquireOutcome = lockStore.TryAcquire(lockPath, candidate, nowUtc);
        if (!acquireOutcome.Acquired)
        {
            var code = acquireOutcome.Reason switch
            {
                AcquireFailureReason.Held => "lock_held",
                AcquireFailureReason.Stale => "lock_stale",
                _ => "lock_unreadable",
            };
            return EmitMergePlanError(rootId, itemId, resolvedParent, prNumber, code,
                $"Could not acquire run lock at '{lockPath}' (reason: {acquireOutcome.Reason?.ToString().ToLowerInvariant() ?? "unknown"}).",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug);
        }

        bool lockReleased = false;
        try
        {
            return await MergePlanPrUnderLockAsync(
                rootId, itemId, resolvedParent, prNumber, isRootPlan, itemKey,
                headBranch, baseBranch, manifestBranch, manifestPath, slug,
                lockToken, admin, ancestorIds, ct).ConfigureAwait(false);
        }
        finally
        {
            // Best-effort release; LockReleased on the result is set inside the success path.
            // If we throw here the verb-level exception handler still surfaces the failure.
            try
            {
                var release = lockStore.TryRelease(lockPath, lockToken);
                lockReleased = release.Released;
            }
            catch
            {
                lockReleased = false;
            }

            if (!lockReleased)
            {
                Console.Error.WriteLine(
                    $"WARNING: failed to release run lock at '{lockPath}' (token={lockToken}); run `polyphony lock force-release --root-id {rootId}` if needed.");
            }
        }
    }

    private async Task<int> MergePlanPrUnderLockAsync(
        int rootId,
        int itemId,
        int parentItemId,
        int prNumber,
        bool isRootPlan,
        string itemKey,
        string headBranch,
        string baseBranch,
        string manifestBranch,
        string manifestPath,
        string slug,
        string lockToken,
        bool admin,
        string ancestorIds,
        CancellationToken ct)
    {
        // ── 4. Verify clean worktree. ──────────────────────────────────────
        try
        {
            var status = await git.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.Count > 0)
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "worktree_dirty",
                    $"Worktree is not clean ({status.Count} entries from `git status --porcelain`); commit, stash, or discard local changes before retrying.",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "internal_error",
                $"Could not read worktree status: {ex.Message}",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken);
        }

        // ── 5. Fetch the feature branch. ───────────────────────────────────
        try
        {
            await git.FetchAsync("origin", manifestBranch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "internal_error",
                $"git fetch origin {manifestBranch} failed: {ex.Message}",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken);
        }

        // ── 6. Poll PR + validate identity. ────────────────────────────────
        GhPullRequestPollData? poll;
        try
        {
            poll = await gh.GetPullRequestPollDataAsync(slug, prNumber, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "internal_error",
                $"gh pr view failed: {ex.Message}",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken);
        }
        if (poll is null)
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "pr_not_found",
                $"PR #{prNumber} not found on {slug}.",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken);

        if (!string.Equals(poll.HeadRefName, headBranch, StringComparison.Ordinal))
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "head_ref_mismatch",
                $"PR #{prNumber} head ref is '{poll.HeadRefName ?? "<null>"}' but the verb expected '{headBranch}'. Refusing to act on the wrong PR.",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                prState: poll.State);

        if (!string.Equals(poll.BaseRefName, baseBranch, StringComparison.Ordinal))
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "base_ref_mismatch",
                $"PR #{prNumber} base ref is '{poll.BaseRefName ?? "<null>"}' but the verb expected '{baseBranch}'. Refusing to act on the wrong PR.",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                prState: poll.State);

        // ── 6b. Stale-generation refusal (P6). Only meaningful for OPEN
        // PRs — for MERGED PRs the merge already happened and we'd just
        // be running the recovery path. Root plans skip the check (no
        // ancestors). Snapshot lives in the PR body's front-matter.
        if (string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase) && !isRootPlan)
        {
            var snapshot = PlanPrFrontMatter.Parse(poll.Body).AncestorPlanGenerations;

            // Read the current manifest from the feature-branch tip without
            // disturbing the worktree. If the file doesn't exist there yet,
            // there's nothing to be stale against — fall through.
            string? manifestYaml = null;
            try
            {
                manifestYaml = await git.ShowFileAtRefAsync($"origin/{manifestBranch}", manifestPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "internal_error",
                    $"Could not read manifest at origin/{manifestBranch}:{manifestPath} for staleness check: {ex.Message}",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                    prState: poll.State);
            }

            if (manifestYaml is not null)
            {
                IReadOnlyDictionary<string, int> currentGens;
                try
                {
                    var remoteManifest = RunManifestStore.Parse(manifestYaml);
                    currentGens = remoteManifest.PlanGenerations;
                }
                catch (Exception ex)
                {
                    return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "internal_error",
                        $"Could not parse manifest at origin/{manifestBranch}:{manifestPath} for staleness check: {ex.Message}",
                        isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                        prState: poll.State);
                }

                var staleness = PlanGenerationStaleness.Check(snapshot, currentGens);
                if (staleness.IsEmpty)
                {
                    // Descendant plan PR with no snapshot in body — refuse.
                    // Root plans were already excluded above, so a descendant
                    // without a snapshot indicates a hand-opened PR or one
                    // pre-dating P3; we can't verify its safety.
                    return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "stale_generation",
                        $"PR #{prNumber} body has no ancestor_plan_generations snapshot in front-matter; descendant plan PRs must carry a snapshot to be merged safely. Re-open the PR via `polyphony pr open-plan-pr` to embed the current snapshot.",
                        isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                        prState: poll.State);
                }

                if (staleness.IsStale)
                {
                    var staleEntries = staleness.StaleEntries
                        .Select(e => new StaleAncestorEntry
                        {
                            AncestorKey = e.AncestorKey,
                            SnapshotGeneration = e.SnapshotGeneration,
                            CurrentGeneration = e.CurrentGeneration,
                        })
                        .ToList();

                    return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "stale_generation",
                        $"PR #{prNumber} ancestor plan-generation snapshot is stale vs the current manifest on origin/{manifestBranch}. Stale entries: {PlanGenerationStaleness.FormatStaleEntries(staleness.StaleEntries)}. Re-open the PR with the current snapshot before merging.",
                        isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                        prState: poll.State, staleAncestors: staleEntries);
                }
            }
        }

        // ── 6c. P8b diff-validation guard. Only meaningful for OPEN PRs
        // (a MERGED PR's diff is locked). Classifies the PR's changed
        // files against the plan-tree taxonomy and refuses on Blocking
        // severity. Shares the PlanDiffValidator helper with the
        // standalone advisory verb `polyphony pr validate-plan-diff` so
        // the merge-time verdict cannot drift from what reviewers saw.
        if (string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<GhPullRequestChangedFile>? changedFiles;
            try
            {
                changedFiles = await gh.GetPullRequestFilesAsync(slug, prNumber, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "internal_error",
                    $"Could not fetch PR #{prNumber} changed files for diff validation: {ex.Message}",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                    prState: poll.State);
            }

            if (changedFiles is null)
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "pr_not_found",
                    $"PR #{prNumber} files endpoint returned no payload during diff validation.",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                    prState: poll.State);

            var strictFm = PlanPrFrontMatter.ParseStrict(poll.Body);
            var changedPaths = changedFiles.Select(f => f.Path).ToList();
            var selfPlanFile = PlanFilePath(itemId);
            string? parentPlanFile = (isRootPlan || parentItemId == 0) ? null : PlanFilePath(parentItemId);
            var ancestorPlanFiles = ParseAncestorIds(ancestorIds, rootId, parentItemId)
                .Select(PlanFilePath)
                .ToList();

            var classification = PlanDiffValidator.Check(
                changedPaths,
                selfPlanFile,
                parentPlanFile,
                ancestorPlanFiles,
                strictFm.RequestsParentChange,
                strictFm.Status);

            if (classification.Severity == ValidationSeverity.Blocking)
            {
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "validation_blocked",
                    $"PR #{prNumber} fails plan-diff validation ({classification.Code}): {classification.Message}",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                    prState: poll.State);
            }
            // Warning + None pass through to the merge step. The advisory
            // verb (validate-plan-diff) is responsible for surfacing
            // warnings to reviewers; the merge-time guard only enforces
            // blocking severities.
        }

        // ── 7. Branch on PR state. ─────────────────────────────────────────
        string mergeCommit;
        bool alreadyMerged;
        if (string.Equals(poll.State, "MERGED", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(poll.MergeCommitSha))
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "missing_merge_commit",
                    $"PR #{prNumber} reports state MERGED but the platform did not return a merge commit SHA.",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                    prState: poll.State);
            mergeCommit = poll.MergeCommitSha;
            alreadyMerged = true;
        }
        else if (string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            GhMergeResult mergeResult;
            try
            {
                // Plan PRs are pinned to merge-commit per ADR Rev 4 — head/base
                // not deleted; sibling plan branches may still be in flight.
                mergeResult = await gh.MergePullRequestAsync(
                    slug, prNumber, GhMergeMethod.Merge,
                    admin: admin, deleteBranch: false,
                    matchHeadCommit: poll.HeadRefOid,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "merge_failed",
                    $"gh pr merge failed: {ex.Message}",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                    prState: poll.State);
            }
            if (!mergeResult.Succeeded)
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "merge_failed",
                    $"gh pr merge did not succeed: {mergeResult.Detail}",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                    prState: poll.State);
            if (string.IsNullOrEmpty(mergeResult.MergeSha))
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "missing_merge_commit",
                    "gh pr merge returned without a merge commit SHA; cannot record the merge in the ledger.",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                    prState: "MERGED");
            mergeCommit = mergeResult.MergeSha;
            alreadyMerged = mergeResult.AlreadyMerged;
        }
        else
        {
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "pr_state_unmergeable",
                $"PR #{prNumber} is in state '{poll.State}'; only OPEN or MERGED are actionable.",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                prState: poll.State);
        }

        // ── 8. Checkout feature branch + reset to remote tip. ──────────────
        // We re-fetch here because step 5's fetch happened BEFORE the gh-API
        // merge in step 7, which advanced origin/{manifestBranch} on the
        // server side (the merge commit landed there). Without a second
        // fetch, our local origin/{manifestBranch} ref is stale and the
        // post-merge manifest commit will fail to push as non-fast-forward.
        try
        {
            await git.FetchAsync("origin", manifestBranch, ct).ConfigureAwait(false);
            await git.CheckoutAsync(manifestBranch, ct).ConfigureAwait(false);
            await git.ResetHardAsync($"origin/{manifestBranch}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // We've already merged — surface the partial success so the caller can retry the manifest half.
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "internal_error",
                $"Could not checkout/reset {manifestBranch}: {ex.Message}",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                prState: "MERGED", merged: true, alreadyMerged: alreadyMerged, mergeCommit: mergeCommit);
        }

        // ── 9. Apply ledger; save+stage+commit+push only on fresh entry. ───
        RunManifest manifest;
        try
        {
            manifest = RunManifestStore.LoadOrThrow(manifestPath);
        }
        catch (Exception ex)
        {
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "internal_error",
                $"Could not load manifest at '{manifestPath}': {ex.Message}",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                prState: "MERGED", merged: true, alreadyMerged: alreadyMerged, mergeCommit: mergeCommit);
        }

        var ledger = ManifestPlanLedger.Apply(manifest, itemKey, prNumber, mergeCommit, DateTime.UtcNow);
        if (ledger.ConflictReason is not null)
            return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "ledger_conflict", ledger.ConflictReason,
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                prState: "MERGED", merged: true, alreadyMerged: alreadyMerged, mergeCommit: mergeCommit,
                prevGen: ledger.PreviousGeneration, currGen: ledger.CurrentGeneration);

        bool manifestRecorded = ledger.Recorded;
        bool manifestPushed = false;

        if (manifestRecorded)
        {
            try
            {
                RunManifestStore.Save(manifestPath, manifest);
                await git.StageAsync(manifestPath, ct).ConfigureAwait(false);
                await git.CommitAsync(
                    $"chore(manifest): record plan PR #{prNumber} merge for {itemKey}", ct).ConfigureAwait(false);
                await git.PushAsync(manifestBranch, "origin", ct).ConfigureAwait(false);
                manifestPushed = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (ExternalToolException pushEx) when (pushEx.Stderr?.Contains("rejected", StringComparison.OrdinalIgnoreCase) == true
                                                       || pushEx.Stderr?.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Roll back the local checkout so a retry sees a clean tip.
                try { await git.ResetHardAsync($"origin/{manifestBranch}", ct).ConfigureAwait(false); }
                catch { /* best-effort; surface the original push failure */ }

                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "manifest_push_rejected",
                    $"Manifest push to origin/{manifestBranch} rejected (likely a concurrent push). Re-run the verb to retry; the ledger will pick up the existing merge commit and bump exactly once. Detail: {pushEx.Stderr}",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                    prState: "MERGED", merged: true, alreadyMerged: alreadyMerged, mergeCommit: mergeCommit,
                    prevGen: ledger.PreviousGeneration, currGen: ledger.CurrentGeneration,
                    manifestRecorded: false, manifestPushed: false);
            }
            catch (Exception ex)
            {
                return EmitMergePlanError(rootId, itemId, parentItemId, prNumber, "internal_error",
                    $"Manifest commit/push failed: {ex.Message}",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, slug: slug, lockToken: lockToken,
                    prState: "MERGED", merged: true, alreadyMerged: alreadyMerged, mergeCommit: mergeCommit,
                    prevGen: ledger.PreviousGeneration, currGen: ledger.CurrentGeneration,
                    manifestRecorded: false, manifestPushed: false);
            }
        }

        // ── 10. Emit success. ──────────────────────────────────────────────
        EmitMergePlan(new PrMergePlanPrResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            ItemKey = itemKey,
            IsRootPlan = isRootPlan,
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            ManifestBranch = manifestBranch,
            RepoSlug = slug,
            PrNumber = prNumber,
            PrState = "MERGED",
            Merged = true,
            AlreadyMerged = alreadyMerged,
            MergeCommit = mergeCommit,
            ManifestRecorded = manifestRecorded,
            ManifestPushed = manifestPushed,
            PreviousGeneration = ledger.PreviousGeneration,
            CurrentGeneration = ledger.CurrentGeneration,
            LockToken = lockToken,
            LockReleased = false,  // set by the caller after release; emitted as false here so workflows treat success+leaked-lock as a workflow-warning-not-failure case
            ErrorCode = "",
        });
        return ExitCodes.Success;
    }

    private async Task<string> SafeResolveRepoRootAsync(CancellationToken ct)
    {
        try { return await lockPathResolver.ResolveRepoRootAsync(ct).ConfigureAwait(false); }
        catch { return ""; }
    }

    private static int EmitMergePlanError(
        int rootId,
        int itemId,
        int parentItemId,
        int prNumber,
        string errorCode,
        string message,
        bool isRootPlan = false,
        string itemKey = "",
        string headBranch = "",
        string baseBranch = "",
        string manifestBranch = "",
        string slug = "",
        string lockToken = "",
        string prState = "",
        bool merged = false,
        bool alreadyMerged = false,
        string mergeCommit = "",
        int prevGen = 0,
        int currGen = 0,
        bool manifestRecorded = false,
        bool manifestPushed = false,
        IReadOnlyList<StaleAncestorEntry>? staleAncestors = null)
    {
        EmitMergePlan(new PrMergePlanPrResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            ItemKey = itemKey,
            IsRootPlan = isRootPlan,
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            ManifestBranch = manifestBranch,
            RepoSlug = slug,
            PrNumber = prNumber,
            PrState = prState,
            Merged = merged,
            AlreadyMerged = alreadyMerged,
            MergeCommit = mergeCommit,
            ManifestRecorded = manifestRecorded,
            ManifestPushed = manifestPushed,
            PreviousGeneration = prevGen,
            CurrentGeneration = currGen,
            LockToken = lockToken,
            LockReleased = false,
            ErrorCode = errorCode,
            Error = message,
            StaleAncestors = staleAncestors,
        });
        return ExitCodes.Success;  // routing-style: workflow branches on ErrorCode, not exit code
    }

    private static void EmitMergePlan(PrMergePlanPrResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrMergePlanPrResult));
}
