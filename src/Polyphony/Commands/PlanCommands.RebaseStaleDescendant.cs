using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;
using Polyphony.Locking;
using Polyphony.Manifest;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony plan rebase-stale-descendant</c> — the heart of the
/// Phase 3 P9 cascade-remedy. Auto-rebases an open descendant plan PR
/// whose <c>ancestor_plan_generations</c> snapshot is behind the current
/// manifest, pushes the new head with a strict lease, rewrites the PR
/// body's snapshot front-matter, records the rebase in the manifest's
/// rebase ledger under reason <c>child_plan_drift</c>, and posts a
/// best-effort comment.
///
/// <para>Implementation follows the 18-step compound transactional
/// sequence in the design doc (P9 cascade remedy, Rev 2 post rubber-duck).
/// All sequence-relevant invariants are preserved:</para>
/// <list type="bullet">
///   <item><b>Lock-before-read</b>: same-root run lock at <c>.polyphony/locks/run-{rootId}.lock</c> acquired before any read of the manifest, so concurrent rebases against the same root cannot race.</item>
///   <item><b>Poll-then-fetch-then-verify</b>: PR poll captures <c>headRefOid</c> first, then <c>git fetch</c>, then <c>rev-parse origin/{head}</c> must match — guards against the head moving between fetch and poll.</item>
///   <item><b>Cascade-precondition</b>: refuses with <c>parent_stale</c> if the parent plan PR's snapshot is itself behind the manifest — rebasing a descendant onto a stale parent would just re-stage the staleness.</item>
///   <item><b>Three-fact noop</b>: <c>noop</c> outcome only when (a) <c>origin/{parent}</c> is already an ancestor of <c>origin/{head}</c>, (b) the body snapshot matches the manifest, and (c) the rebase ledger has a matching <c>(branch, commit, child_plan_drift)</c> entry.</item>
///   <item><b>Strict body update</b>: body uses <see cref="PlanPrFrontMatter.ParseStrict"/> + <see cref="PlanPrFrontMatter.ReplaceSnapshotPreservingTail"/> so a malformed body never gets silently overwritten and the existing <c>requests_parent_change</c> + body tail are preserved byte-for-byte.</item>
///   <item><b>Partial-success replay</b>: <c>body_update_failed</c> and <c>manifest_push_rejected</c> leave the manifest unchanged, so a re-run completes the recovery via the three-fact missing-piece path.</item>
/// </list>
///
/// <para>Always exits 0 (routing-style verb). Workflows route on
/// <see cref="PlanRebaseStaleDescendantResult.Outcome"/> /
/// <see cref="PlanRebaseStaleDescendantResult.ErrorCode"/>.</para>
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>
    /// Auto-rebase a stale descendant plan PR onto the current parent-plan
    /// tip. See class summary for the full 18-step sequence and outcome
    /// taxonomy.
    /// </summary>
    /// <param name="rootId">Root work item id (positive).</param>
    /// <param name="itemId">Descendant work item id whose PR is being rebased. MUST NOT equal <paramref name="rootId"/> (the verb refuses root-plan PR rebases).</param>
    /// <param name="parentItemId">Immediate plan-tree parent's work-item id. Use <paramref name="rootId"/> when the parent is the root plan.</param>
    /// <param name="prNumber">PR number to rebase (positive).</param>
    /// <param name="ancestorIds">Comma-separated ancestor chain BELOW the root, ending in the literal token <c>root</c> (e.g. <c>"5678,root"</c> for a grandchild). Empty when the parent IS the root plan and the chain has only "root".</param>
    /// <param name="manifestPath">Path to the run manifest. Defaults to <c>.polyphony/run.yaml</c>.</param>
    /// <param name="by">Lock acquirer name; defaults to <c>USERNAME</c>/<c>USER</c> env.</param>
    /// <param name="lockTtlHours">Run-lock TTL (default 24).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("rebase-stale-descendant")]
    public async Task<int> RebaseStaleDescendant(
        int rootId,
        int itemId,
        int parentItemId,
        int prNumber,
        string ancestorIds = "",
        string manifestPath = ".polyphony/run.yaml",
        string by = "",
        int lockTtlHours = 24,
        CancellationToken ct = default)
    {
        // ── 1. Validate inputs + derive head/parent branches. ──────────────
        if (!Polyphony.Branching.RootId.TryParse(rootId, out var root))
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--root-id must be positive (got {rootId})");

        if (!WorkItemId.TryParse(itemId, out var item))
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--item-id must be positive (got {itemId})");

        if (itemId == rootId)
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--item-id ({itemId}) equals --root-id; this verb does not handle root-plan PR rebases.");

        if (parentItemId <= 0)
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--parent-item-id must be positive (got {parentItemId})");

        if (parentItemId == itemId)
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--parent-item-id ({parentItemId}) must not equal --item-id; a plan cannot be its own parent.");

        if (prNumber <= 0)
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--pr-number must be positive (got {prNumber})");

        var headBranch = BranchNameBuilder.DescendantPlan(root, item).Value;
        string parentPlanBranch;
        if (parentItemId == rootId)
        {
            parentPlanBranch = BranchNameBuilder.RootPlan(root).Value;
        }
        else
        {
            if (!WorkItemId.TryParse(parentItemId, out var parentItem))
                return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                    $"--parent-item-id must be positive (got {parentItemId})");
            parentPlanBranch = BranchNameBuilder.DescendantPlan(root, parentItem).Value;
        }
        var manifestBranch = BranchNameBuilder.Feature(root).Value;

        // ── 2. Resolve repo slug. ──────────────────────────────────────────
        var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(slug))
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "no_slug",
                "Could not resolve repo slug from origin remote",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch);

        // ── 3. Acquire same-root run lock BEFORE any reads. ────────────────
        // RunLockStore is stateless; resolver only needs IGitClient. Construct
        // locally so PlanCommands' ConsoleAppFramework primary ctor stays
        // unchanged (CAF011 forbids multiple ctors).
        var lockStore = new RunLockStore();
        var lockPathResolver = new RunLockPathResolver(git);

        string lockPath;
        try
        {
            lockPath = await lockPathResolver.ResolveAsync(rootId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "internal_error", ex.Message,
                headBranch: headBranch, parentPlanBranch: parentPlanBranch);
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
            RepoRoot = await SafeResolveRepoRootAsync(lockPathResolver, ct).ConfigureAwait(false) ?? string.Empty,
        };

        var acquireOutcome = lockStore.TryAcquire(lockPath, candidate, nowUtc);
        if (!acquireOutcome.Acquired)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "lock_held",
                $"Could not acquire run lock at '{lockPath}' (reason: {acquireOutcome.Reason?.ToString().ToLowerInvariant() ?? "unknown"}).",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }

        try
        {
            return await RebaseStaleDescendantUnderLockAsync(
                rootId, itemId, parentItemId, prNumber,
                root, item, headBranch, parentPlanBranch, manifestBranch,
                manifestPath, slug, ancestorIds, ct).ConfigureAwait(false);
        }
        finally
        {
            try { lockStore.TryRelease(lockPath, lockToken); } catch { /* best-effort */ }
        }
    }

    private async Task<int> RebaseStaleDescendantUnderLockAsync(
        int rootId,
        int itemId,
        int parentItemId,
        int prNumber,
        Polyphony.Branching.RootId root,
        WorkItemId item,
        string headBranch,
        string parentPlanBranch,
        string manifestBranch,
        string manifestPath,
        string slug,
        string ancestorIds,
        CancellationToken ct)
    {
        // ── 4. Verify clean worktree. ──────────────────────────────────────
        try
        {
            var status = await git.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.Count > 0)
                return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "worktree_dirty",
                    $"Worktree is not clean ({status.Count} entries from `git status --porcelain`); commit, stash, or discard local changes before retrying.",
                    headBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "internal_error",
                $"Could not read worktree status: {ex.Message}",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }

        // ── 5. Fetch feature branch + parent plan branch. ──────────────────
        try
        {
            await git.FetchAsync("origin", manifestBranch, ct).ConfigureAwait(false);
            await git.FetchAsync("origin", parentPlanBranch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "git_failed",
                $"git fetch failed: {ex.Message}",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }

        // ── 6. Read manifest from origin/feature/{root} UNDER lock. ────────
        RunManifest manifest;
        try
        {
            var manifestYaml = await git.ShowFileAtRefAsync($"origin/{manifestBranch}", manifestPath, ct).ConfigureAwait(false);
            if (manifestYaml is null)
                return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "manifest_read_failed",
                    $"manifest not found at origin/{manifestBranch}:{manifestPath}",
                    headBranch: headBranch, parentPlanBranch: parentPlanBranch);
            manifest = RunManifestStore.Parse(manifestYaml, manifestPath);
            RunManifestValidator.ValidateOrThrow(manifest);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExternalToolException ex)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "manifest_read_failed",
                $"manifest read failed: {ex.Message}",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }
        catch (Exception ex)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "manifest_invalid",
                $"manifest invalid: {ex.Message}",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }

        if (manifest.RootId != rootId)
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "manifest_invalid",
                $"manifest at origin/{manifestBranch}:{manifestPath} declares root {manifest.RootId}, expected {rootId}",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch);

        // ── 7. Poll PR + validate identity + state. ────────────────────────
        GhPullRequestPollData? poll;
        try
        {
            poll = await gh.GetPullRequestPollDataAsync(slug, prNumber, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "gh_failed",
                $"gh pr view failed: {ex.Message}",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }
        if (poll is null)
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "pr_not_found",
                $"PR #{prNumber} not found on {slug}.",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch);

        if (!string.Equals(poll.HeadRefName, headBranch, StringComparison.Ordinal))
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "pr_identity_mismatch",
                $"PR #{prNumber} head ref is '{poll.HeadRefName ?? "<null>"}' but the verb expected '{headBranch}'.",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch, prUrl: $"https://github.com/{slug}/pull/{prNumber}");

        if (!string.Equals(poll.BaseRefName, parentPlanBranch, StringComparison.Ordinal))
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "pr_identity_mismatch",
                $"PR #{prNumber} base ref is '{poll.BaseRefName ?? "<null>"}' but the verb expected '{parentPlanBranch}'.",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch, prUrl: $"https://github.com/{slug}/pull/{prNumber}");

        if (!string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "pr_state_invalid",
                $"PR #{prNumber} is in state '{poll.State}'; only OPEN PRs are eligible for cascade rebase.",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch, prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                oldHeadSha: poll.HeadRefOid);

        var polledSha = poll.HeadRefOid ?? string.Empty;
        if (string.IsNullOrEmpty(polledSha))
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "pr_state_invalid",
                $"PR #{prNumber} returned no headRefOid from gh; cannot proceed with a force-with-lease push.",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch, prUrl: $"https://github.com/{slug}/pull/{prNumber}");

        // ── 8. Fetch PR head + verify origin/{head} == polled SHA. ─────────
        // The poll-then-fetch ordering means a head that moved server-side
        // BETWEEN poll and fetch is detected here as a SHA mismatch.
        try
        {
            await git.FetchAsync("origin", headBranch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "git_failed",
                $"git fetch origin {headBranch} failed: {ex.Message}",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch, prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                oldHeadSha: polledSha);
        }

        var fetchedHeadSha = await ResolveRefSilentlyAsync($"origin/{headBranch}", ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(fetchedHeadSha)
            || !string.Equals(fetchedHeadSha, polledSha, StringComparison.OrdinalIgnoreCase))
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "pr_head_changed",
                $"origin/{headBranch} resolved to '{fetchedHeadSha ?? "<unresolved>"}' but the PR poll reported head SHA '{polledSha}'. The head moved between poll and fetch — re-run after the workflow re-classifies.",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                prUrl: $"https://github.com/{slug}/pull/{prNumber}", oldHeadSha: polledSha);
        }

        // ── 9. Cascade-precondition: parent plan branch must be fresh. ─────
        // The parent is fresh iff (a) it has no open plan PR, OR (b) its
        // open plan PR's snapshot matches the manifest. Rebasing onto a
        // stale parent would just re-stage the staleness.
        var parentFreshness = await CheckParentFreshnessAsync(slug, parentPlanBranch, manifest, ct).ConfigureAwait(false);
        if (parentFreshness is { } parentMessage)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "parent_stale", parentMessage,
                headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                prUrl: $"https://github.com/{slug}/pull/{prNumber}", oldHeadSha: polledSha);
        }

        // ── 10. Three-fact freshness check. ────────────────────────────────
        var snapshotInBody = PlanPrFrontMatter.Parse(poll.Body).AncestorPlanGenerations;
        var ancestorKeys = ParseAncestorKeys(ancestorIds, snapshotInBody);
        var desiredSnapshot = ProjectManifestOntoAncestors(manifest.PlanGenerations, ancestorKeys, snapshotInBody);

        var bodyFresh = SnapshotsEquivalent(snapshotInBody, desiredSnapshot);

        bool branchFresh;
        try
        {
            branchFresh = await git.IsAncestorAsync($"origin/{parentPlanBranch}", $"origin/{headBranch}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "git_failed",
                $"git merge-base --is-ancestor failed: {ex.Message}",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                prUrl: $"https://github.com/{slug}/pull/{prNumber}", oldHeadSha: polledSha);
        }

        bool ledgerFresh = manifest.Rebases.Any(r =>
            string.Equals(r.Branch, headBranch, StringComparison.Ordinal)
            && string.Equals(r.Commit, polledSha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Reason, "child_plan_drift", StringComparison.Ordinal));

        if (branchFresh && bodyFresh && ledgerFresh)
        {
            EmitRebase(new PlanRebaseStaleDescendantResult
            {
                RootId = rootId,
                ItemId = itemId,
                ParentItemId = parentItemId,
                PrNumber = prNumber,
                PrUrl = $"https://github.com/{slug}/pull/{prNumber}",
                HeadBranch = headBranch,
                ParentPlanBranch = parentPlanBranch,
                Outcome = "noop",
                OldHeadSha = polledSha,
                NewHeadSha = polledSha,
            });
            return ExitCodes.Success;
        }

        // ── 11-13. Rebase + push (only if branch is stale). ────────────────
        // When branch is fresh but body or ledger is missing, skip the
        // rebase+push entirely and recover via steps 14-16. This is the
        // partial-success replay path.
        string newHeadSha = polledSha;
        bool ranRebase = false;
        if (!branchFresh)
        {
            var oldBase = await git.MergeBaseAsync(polledSha, $"origin/{parentPlanBranch}", ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(oldBase))
            {
                return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "git_failed",
                    $"git merge-base could not find a common ancestor between {polledSha} and origin/{parentPlanBranch}.",
                    headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                    prUrl: $"https://github.com/{slug}/pull/{prNumber}", oldHeadSha: polledSha);
            }

            RebaseOutcome rebaseOutcome;
            try
            {
                rebaseOutcome = await git.RebaseOntoAsync($"origin/{parentPlanBranch}", oldBase, polledSha, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "git_failed",
                    $"git rebase failed: {ex.Message}",
                    headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                    prUrl: $"https://github.com/{slug}/pull/{prNumber}", oldHeadSha: polledSha);
            }

            switch (rebaseOutcome)
            {
                case RebaseOutcome.Conflict conflict:
                    EmitRebase(new PlanRebaseStaleDescendantResult
                    {
                        RootId = rootId,
                        ItemId = itemId,
                        ParentItemId = parentItemId,
                        PrNumber = prNumber,
                        PrUrl = $"https://github.com/{slug}/pull/{prNumber}",
                        HeadBranch = headBranch,
                        ParentPlanBranch = parentPlanBranch,
                        Outcome = "conflict",
                        OldHeadSha = polledSha,
                        ConflictFiles = conflict.Files,
                        Error = $"Rebase produced merge conflicts in {conflict.Files.Count} file(s); aborted. Workflow should route to human_gate.",
                        ErrorCode = "rebase_conflict",
                    });
                    return ExitCodes.Success;

                case RebaseOutcome.Failed failed:
                    return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "rebase_failed",
                        $"Rebase failed: {failed.Stderr}",
                        headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                        prUrl: $"https://github.com/{slug}/pull/{prNumber}", oldHeadSha: polledSha);

                case RebaseOutcome.Clean clean:
                    newHeadSha = clean.NewHeadSha;
                    ranRebase = true;
                    break;
            }

            // Push HEAD to the head branch with strict lease.
            var pushResult = await git.PushHeadWithLeaseAsync("origin", headBranch, polledSha, ct).ConfigureAwait(false);
            if (!pushResult.Succeeded)
            {
                var stderr = pushResult.Stderr ?? string.Empty;
                if (LooksLikeLeaseFailure(stderr))
                {
                    return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "pr_head_changed",
                        $"Force-with-lease push rejected (head moved between poll and push). Detail: {stderr}",
                        headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                        prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                        oldHeadSha: polledSha, newHeadSha: newHeadSha);
                }
                return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "git_failed",
                    $"git push --force-with-lease failed (exit {pushResult.ExitCode}): {stderr}",
                    headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                    prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                    oldHeadSha: polledSha, newHeadSha: newHeadSha);
            }
        }

        // ── 14. Update PR body front-matter (idempotent — skip if fresh). ──
        // Always re-read the current body via ParseStrict so we refuse cleanly
        // on malformed input rather than silently overwriting it.
        bool bodyUpdated = false;
        if (!bodyFresh)
        {
            var strict = PlanPrFrontMatter.ParseStrict(poll.Body);
            if (strict.Status == FrontMatterStatus.Malformed)
            {
                return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "malformed_front_matter",
                    $"PR body's front-matter is malformed: {strict.ErrorDetail ?? "<no detail>"}. Refusing to overwrite a hand-edited body — operator must reconcile manually.",
                    headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                    prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                    oldHeadSha: polledSha, newHeadSha: ranRebase ? newHeadSha : null);
            }

            var replacement = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(poll.Body, desiredSnapshot);
            switch (replacement)
            {
                case FrontMatterReplacement.Malformed malformed:
                    return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "malformed_front_matter",
                        $"PR body's front-matter is malformed: {malformed.Reason}",
                        headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                        prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                        oldHeadSha: polledSha, newHeadSha: ranRebase ? newHeadSha : null);

                case FrontMatterReplacement.Absent:
                    return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "malformed_front_matter",
                        "PR body has no fenced front-matter at the start; refusing to invent one (a hand-written plan PR is out of scope for the cascade remedy).",
                        headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                        prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                        oldHeadSha: polledSha, newHeadSha: ranRebase ? newHeadSha : null);

                case FrontMatterReplacement.Replaced replaced:
                    bool edited;
                    try
                    {
                        edited = await gh.EditPullRequestBodyAsync(slug, prNumber, replaced.NewBody, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "body_update_failed",
                            $"gh pr edit failed: {ex.Message}. Branch was pushed; manifest NOT recorded — replay the verb to complete recovery.",
                            headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                            prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                            oldHeadSha: polledSha, newHeadSha: ranRebase ? newHeadSha : null);
                    }

                    if (!edited)
                    {
                        return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "body_update_failed",
                            "gh pr edit returned non-success. Branch was pushed; manifest NOT recorded — replay the verb to complete recovery.",
                            headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                            prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                            oldHeadSha: polledSha, newHeadSha: ranRebase ? newHeadSha : null);
                    }
                    bodyUpdated = true;
                    break;
            }
        }

        // ── 15-16. Re-load manifest from origin tip + apply ledger + push. ─
        // Follows the MergePlanPr pattern: checkout manifestBranch + reset to
        // origin's tip + LoadOrThrow gives us a manifest that already reflects
        // any concurrent pushes that happened between step 6 and now, so our
        // Save will not silently overwrite peer changes. Apply is idempotent
        // on (branch, commit, reason), so a concurrent push that recorded the
        // SAME entry leaves us with DuplicateSkipped → no commit/push needed.
        bool manifestRecorded = false;
        bool manifestPushed = false;
        try
        {
            await git.CheckoutAsync(manifestBranch, ct).ConfigureAwait(false);
            await git.ResetHardAsync($"origin/{manifestBranch}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "internal_error",
                $"Could not checkout/reset {manifestBranch}: {ex.Message}",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                oldHeadSha: polledSha, newHeadSha: ranRebase ? newHeadSha : null,
                bodyUpdated: bodyUpdated);
        }

        RunManifest manifestForLedger;
        try
        {
            manifestForLedger = RunManifestStore.LoadOrThrow(manifestPath);
        }
        catch (Exception ex)
        {
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "manifest_read_failed",
                $"Could not reload manifest at '{manifestPath}' after reset: {ex.Message}",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                oldHeadSha: polledSha, newHeadSha: ranRebase ? newHeadSha : null,
                bodyUpdated: bodyUpdated);
        }

        var ledgerOutcome = ManifestRebaseLedger.Apply(manifestForLedger, headBranch, newHeadSha, "child_plan_drift", DateTime.UtcNow);
        if (ledgerOutcome is RebaseLedgerOutcome.Appended appended)
        {
            // Patch in the parent refname — Apply intentionally leaves Onto empty.
            appended.Record.Onto = $"refs/heads/{parentPlanBranch}";
            manifestRecorded = true;
        }
        else if (ledgerOutcome is RebaseLedgerOutcome.InvalidReason)
        {
            // Should be unreachable — "child_plan_drift" is in the allow-list.
            return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "internal_error",
                "ManifestRebaseLedger rejected reason 'child_plan_drift'; this is a bug.",
                headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                oldHeadSha: polledSha, newHeadSha: ranRebase ? newHeadSha : null,
                bodyUpdated: bodyUpdated);
        }
        // DuplicateSkipped: a peer already recorded the same entry. Treat as
        // already-pushed for replay purposes; manifest stayed clean.

        if (manifestRecorded)
        {
            try
            {
                RunManifestStore.Save(manifestPath, manifestForLedger);
                await git.StageAsync(manifestPath, ct).ConfigureAwait(false);
                await git.CommitAsync(
                    $"chore(manifest): record rebase of {headBranch} after ancestor plan_generation drift",
                    ct).ConfigureAwait(false);
                await git.PushAsync(manifestBranch, "origin", ct).ConfigureAwait(false);
                manifestPushed = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (ExternalToolException pushEx) when (LooksLikePushReject(pushEx.Stderr))
            {
                try { await git.ResetHardAsync($"origin/{manifestBranch}", ct).ConfigureAwait(false); }
                catch { /* best-effort rollback */ }
                return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "manifest_push_rejected",
                    $"Manifest push to origin/{manifestBranch} rejected (likely a concurrent push). Re-run the verb to complete recovery — the rebase ledger entry will be re-applied idempotently. Detail: {pushEx.Stderr}",
                    headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                    prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                    oldHeadSha: polledSha, newHeadSha: ranRebase ? newHeadSha : null,
                    bodyUpdated: bodyUpdated);
            }
            catch (Exception ex)
            {
                return EmitRebaseError(rootId, itemId, parentItemId, prNumber, "internal_error",
                    $"Manifest commit/push failed: {ex.Message}",
                    headBranch: headBranch, parentPlanBranch: parentPlanBranch,
                    prUrl: $"https://github.com/{slug}/pull/{prNumber}",
                    oldHeadSha: polledSha, newHeadSha: ranRebase ? newHeadSha : null,
                    bodyUpdated: bodyUpdated);
            }
        }

        // ── 17. Best-effort comment. ───────────────────────────────────────
        var warnings = new List<string>();
        bool commentPosted = false;
        if (ranRebase)
        {
            var commentBody = BuildAutoRebaseComment(parentPlanBranch, manifestForLedger.PlanGenerations, polledSha, newHeadSha);
            try
            {
                commentPosted = await gh.CommentPullRequestAsync(slug, prNumber, commentBody, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                warnings.Add($"comment-post failed: {ex.Message}");
            }
            if (!commentPosted && warnings.Count == 0)
            {
                warnings.Add("comment-post returned non-success (best-effort; rebase still succeeded).");
            }
        }

        EmitRebase(new PlanRebaseStaleDescendantResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            PrNumber = prNumber,
            PrUrl = $"https://github.com/{slug}/pull/{prNumber}",
            HeadBranch = headBranch,
            ParentPlanBranch = parentPlanBranch,
            Outcome = "rebased",
            OldHeadSha = polledSha,
            NewHeadSha = newHeadSha,
            CommentPosted = commentPosted,
            BodyUpdated = bodyUpdated,
            ManifestRecorded = manifestRecorded,
            ManifestPushed = manifestPushed,
            Warnings = warnings,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Returns null when the parent plan branch is fresh (no open PR, or
    /// open PR with a snapshot that matches the manifest's current
    /// generations for the keys it lists). Returns a human-readable
    /// reason string when the parent is stale.
    /// </summary>
    private async Task<string?> CheckParentFreshnessAsync(
        string slug,
        string parentPlanBranch,
        RunManifest manifest,
        CancellationToken ct)
    {
        IReadOnlyList<PullRequestSummary> parentPrs;
        try
        {
            parentPrs = await gh.ListPullRequestsAsync(
                slug,
                new PrListFilters(Head: parentPlanBranch, State: "open", Limit: 5),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Best-effort: can't list parent PRs → treat as fresh (no signal
            // beats a false-positive parent_stale that strands the cascade).
            return null;
        }

        if (parentPrs.Count == 0) return null;  // no open PR on parent → fresh

        // Take the highest-numbered open PR; siblings on the same head are
        // ignored (matches detect-state semantics).
        var parentPr = parentPrs.OrderByDescending(p => p.Number).First();

        GhPullRequestPollData? parentPoll;
        try
        {
            parentPoll = await gh.GetPullRequestPollDataAsync(slug, parentPr.Number, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
        if (parentPoll is null) return null;

        var parentSnapshot = PlanPrFrontMatter.Parse(parentPoll.Body).AncestorPlanGenerations;
        if (parentSnapshot.Count == 0)
        {
            // Root plan PRs (head = plan/{root}) carry no snapshot — they have
            // no ancestors. Treat as fresh.
            return null;
        }

        foreach (var (key, snapshotGen) in parentSnapshot)
        {
            if (manifest.PlanGenerations.TryGetValue(key, out var currentGen) && currentGen > snapshotGen)
            {
                return $"Parent plan PR #{parentPr.Number} on '{parentPlanBranch}' is itself stale: {key} snapshot={snapshotGen}, current={currentGen}. Workflow should remedy the parent first.";
            }
        }
        return null;
    }

    private async Task<string?> SafeResolveRepoRootAsync(RunLockPathResolver resolver, CancellationToken ct)
    {
        try { return await resolver.ResolveRepoRootAsync(ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private async Task<string?> ResolveRefSilentlyAsync(string refspec, CancellationToken ct)
    {
        // Avoid IGitClient.GetTopLevelAsync etc — none of the existing methods
        // do "rev-parse arbitrary ref". Fall back to a direct invocation via
        // the git client's underlying merge-base wrapper would be wrong here.
        // We use the rebase outcome's "git rev-parse origin/{head}" pattern
        // by routing through MergeBaseAsync(ref, ref) which returns the SHA
        // (a ref's merge-base with itself is itself).
        return await git.MergeBaseAsync(refspec, refspec, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> ParseAncestorKeys(
        string ancestorIds,
        IReadOnlyDictionary<string, int> existingSnapshot)
    {
        // Prefer the explicit --ancestor-ids if supplied; otherwise fall
        // back to the keys already present in the existing snapshot. The
        // snapshot's key set is the authoritative ancestor chain at PR-open
        // time — preserving it on the rewrite keeps the body schema stable.
        if (!string.IsNullOrWhiteSpace(ancestorIds))
        {
            var parts = ancestorIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) return parts;
        }
        return existingSnapshot.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyDictionary<string, int> ProjectManifestOntoAncestors(
        IReadOnlyDictionary<string, int> manifestGenerations,
        IReadOnlyList<string> ancestorKeys,
        IReadOnlyDictionary<string, int> existingSnapshot)
    {
        var projected = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in ancestorKeys)
        {
            if (manifestGenerations.TryGetValue(key, out var g))
            {
                projected[key] = g;
            }
            else if (existingSnapshot.TryGetValue(key, out var existing))
            {
                // Manifest doesn't know this key — preserve the snapshot value
                // (better than dropping it; snapshot stays well-formed).
                projected[key] = existing;
            }
            else
            {
                projected[key] = 0;
            }
        }
        return projected;
    }

    private static bool SnapshotsEquivalent(
        IReadOnlyDictionary<string, int> a,
        IReadOnlyDictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other)) return false;
            if (value != other) return false;
        }
        return true;
    }

    private static string BuildAutoRebaseComment(
        string parentPlanBranch,
        IReadOnlyDictionary<string, int> currentGenerations,
        string oldSha,
        string newSha)
    {
        var sb = new StringBuilder();
        sb.Append("🔄 Auto-rebased onto `").Append(parentPlanBranch).Append("` after ancestor `plan_generation` advanced");
        if (currentGenerations.Count > 0)
        {
            sb.Append(" (current: ");
            sb.Append(string.Join(", ",
                currentGenerations.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}")));
            sb.Append(')');
        }
        sb.Append(". `").Append(ShortSha(oldSha)).Append("` → `").Append(ShortSha(newSha)).Append("`.");
        sb.Append("\n\n_This comment was posted by `polyphony plan rebase-stale-descendant` (P9 cascade remedy)._");
        return sb.ToString();
    }

    private static string ShortSha(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    private static bool LooksLikeLeaseFailure(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("stale info", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("force-with-lease", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePushReject(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase);
    }

    private static int EmitRebaseError(
        int rootId,
        int itemId,
        int parentItemId,
        int prNumber,
        string errorCode,
        string message,
        string headBranch = "",
        string parentPlanBranch = "",
        string prUrl = "",
        string? oldHeadSha = null,
        string? newHeadSha = null,
        bool bodyUpdated = false)
    {
        EmitRebase(new PlanRebaseStaleDescendantResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            PrNumber = prNumber,
            PrUrl = prUrl,
            HeadBranch = headBranch,
            ParentPlanBranch = parentPlanBranch,
            Outcome = errorCode,
            OldHeadSha = oldHeadSha,
            NewHeadSha = newHeadSha,
            BodyUpdated = bodyUpdated,
            Error = message,
            ErrorCode = errorCode,
        });
        return ExitCodes.Success;
    }

    private static void EmitRebase(PlanRebaseStaleDescendantResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PlanRebaseStaleDescendantResult));
}
