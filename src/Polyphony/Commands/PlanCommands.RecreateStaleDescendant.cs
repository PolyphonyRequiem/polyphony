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
/// <c>polyphony plan recreate-stale-descendant</c> — the second policy
/// outcome of the Phase 3 P9 cascade-remedy. When a stale descendant plan
/// PR cannot (or should not) be auto-rebased, this verb closes the stale PR,
/// removes its head branch (best-effort), re-creates the plan branch from
/// the current parent-plan tip, opens a fresh PR with an up-to-date
/// <c>ancestor_plan_generations</c> snapshot, and records the recreation in
/// the manifest's rebase ledger under reason <c>child_plan_drift</c>.
///
/// <para>This GitHub-only implementation matches the reach of the rebase
/// sibling shipped in #107. ADO P9 cascade is a separate later workstream.</para>
///
/// <para><b>Compound transactional sequence</b> (mirrors the discipline of
/// <c>plan rebase-stale-descendant</c> step-by-step):</para>
/// <list type="bullet">
///   <item><b>Lock-before-read</b>: same-root run lock at <c>.polyphony/locks/run-{rootId}.lock</c> acquired before any read of the manifest, so concurrent remedies on the same root cannot race.</item>
///   <item><b>Cascade-precondition</b>: refuses with <c>parent_stale</c> if the parent plan PR's snapshot is itself behind the manifest — recreating onto a stale parent would just re-stage the staleness.</item>
///   <item><b>Identity-verified close</b>: PR poll captures head/base refs first; refuses with <c>pr_identity_mismatch</c> if either is not what the verb expects.</item>
///   <item><b>Best-effort branch delete</b>: <c>git push origin --delete {head}</c> failure is a warning, not a terminal error — branch may already have been removed.</item>
///   <item><b>Build-the-replay-safety-net before pushing manifest</b>: branch + PR + manifest mutations land in that order. Failure between any two leaves the verb in a state that re-running can complete via the noop / partial-success paths.</item>
///   <item><b>Three-fact noop</b>: <c>noop</c> outcome only when (a) the old PR is already CLOSED, (b) a fresh OPEN PR exists on the same head with a current snapshot, and (c) the rebase ledger has a matching <c>(branch, sha, child_plan_drift)</c> entry.</item>
/// </list>
///
/// <para>Always exits 0 (routing-style verb). Workflows route on
/// <see cref="PlanRecreateStaleDescendantResult.Outcome"/> /
/// <see cref="PlanRecreateStaleDescendantResult.ErrorCode"/>.</para>
///
/// <para><b>Implementation note</b> — the design doc said this verb would
/// "compose existing <c>pr close</c> + <c>branch ensure-plan</c> +
/// <c>pr open-plan-pr</c>". Reality at PR-author time:</para>
/// <list type="bullet">
///   <item><c>polyphony pr close</c> verb did not exist; we added <see cref="IGhClient.ClosePullRequestAsync"/> instead and consume it directly.</item>
///   <item>The branch-create and PR-open steps are inlined here (using the same git/gh primitives the existing verbs use) rather than calling those verbs as sub-processes — sub-process invocation would emit their JSON envelopes to stdout and pollute this verb's single-result contract.</item>
/// </list>
/// </summary>
public sealed partial class PlanCommands
{
    private static readonly System.Text.RegularExpressions.Regex PrUrlNumberRegex =
        new(@"/pull/(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Close + recreate a stale descendant plan PR. See class summary for
    /// the full sequence and outcome taxonomy.
    /// </summary>
    /// <param name="rootId">Root work item id (positive).</param>
    /// <param name="itemId">Descendant work item id whose PR is being recreated. MUST NOT equal <paramref name="rootId"/> (the verb refuses root-plan PR recreates).</param>
    /// <param name="parentItemId">Immediate plan-tree parent's work-item id. Use <paramref name="rootId"/> when the parent is the root plan.</param>
    /// <param name="prNumber">Old (stale) PR number to close (positive).</param>
    /// <param name="ancestorIds">Comma-separated ancestor chain BELOW the root, ending in the literal token <c>root</c> (e.g. <c>"5678,root"</c> for a grandchild). Empty when the parent IS the root plan and the chain has only "root".</param>
    /// <param name="manifestPath">Path to the run manifest. Defaults to <c>.polyphony/run.yaml</c>.</param>
    /// <param name="by">Lock acquirer name; defaults to <c>USERNAME</c>/<c>USER</c> env.</param>
    /// <param name="lockTtlHours">Run-lock TTL (default 24).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("recreate-stale-descendant")]
    public async Task<int> RecreateStaleDescendant(
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
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--root-id must be positive (got {rootId})");

        if (!WorkItemId.TryParse(itemId, out var item))
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--item-id must be positive (got {itemId})");

        if (itemId == rootId)
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--item-id ({itemId}) equals --root-id; this verb does not handle root-plan PR recreates.");

        if (parentItemId <= 0)
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--parent-item-id must be positive (got {parentItemId})");

        if (parentItemId == itemId)
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                $"--parent-item-id ({parentItemId}) must not equal --item-id; a plan cannot be its own parent.");

        if (prNumber <= 0)
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
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
                return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "invalid_argument",
                    $"--parent-item-id must be positive (got {parentItemId})");
            parentPlanBranch = BranchNameBuilder.DescendantPlan(root, parentItem).Value;
        }
        var manifestBranch = BranchNameBuilder.Feature(root).Value;

        // ── 2. Resolve repo slug. ──────────────────────────────────────────
        var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(slug))
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "no_slug",
                "Could not resolve repo slug from origin remote",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);

        // ── 3. Acquire same-root run lock BEFORE any reads. ────────────────
        var lockStore = new RunLockStore();
        var lockPathResolver = new RunLockPathResolver(git);

        string lockPath;
        try
        {
            lockPath = await lockPathResolver.ResolveAsync(rootId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "internal_error", ex.Message,
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);
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
            RepoRoot = await SafeResolveRepoRootForRecreateAsync(lockPathResolver, ct).ConfigureAwait(false) ?? string.Empty,
        };

        var acquireOutcome = lockStore.TryAcquire(lockPath, candidate, nowUtc);
        if (!acquireOutcome.Acquired)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "lock_held",
                $"Could not acquire run lock at '{lockPath}' (reason: {acquireOutcome.Reason?.ToString().ToLowerInvariant() ?? "unknown"}).",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }

        try
        {
            return await RecreateStaleDescendantUnderLockAsync(
                rootId, itemId, parentItemId, prNumber,
                headBranch, parentPlanBranch, manifestBranch,
                manifestPath, slug, ancestorIds, ct).ConfigureAwait(false);
        }
        finally
        {
            try { lockStore.TryRelease(lockPath, lockToken); } catch { /* best-effort */ }
        }
    }

    private async Task<int> RecreateStaleDescendantUnderLockAsync(
        int rootId,
        int itemId,
        int parentItemId,
        int prNumber,
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
                return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "worktree_dirty",
                    $"Worktree is not clean ({status.Count} entries from `git status --porcelain`); commit, stash, or discard local changes before retrying.",
                    oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "internal_error",
                $"Could not read worktree status: {ex.Message}",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);
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
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "git_failed",
                $"git fetch failed: {ex.Message}",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }

        // ── 6. Read manifest from origin/feature/{root} UNDER lock. ────────
        RunManifest manifest;
        try
        {
            var manifestYaml = await git.ShowFileAtRefAsync($"origin/{manifestBranch}", manifestPath, ct).ConfigureAwait(false);
            if (manifestYaml is null)
                return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "manifest_read_failed",
                    $"manifest not found at origin/{manifestBranch}:{manifestPath}",
                    oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);
            manifest = RunManifestStore.Parse(manifestYaml, manifestPath);
            RunManifestValidator.ValidateOrThrow(manifest);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExternalToolException ex)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "manifest_read_failed",
                $"manifest read failed: {ex.Message}",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }
        catch (Exception ex)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "manifest_invalid",
                $"manifest invalid: {ex.Message}",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }

        if (manifest.RootId != rootId)
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "manifest_invalid",
                $"manifest at origin/{manifestBranch}:{manifestPath} declares root {manifest.RootId}, expected {rootId}",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);

        // ── 7. Poll old PR + verify identity. ──────────────────────────────
        GhPullRequestPollData? poll;
        try
        {
            poll = await gh.GetPullRequestPollDataAsync(slug, prNumber, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "gh_failed",
                $"gh pr view failed: {ex.Message}",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);
        }
        if (poll is null)
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "pr_not_found",
                $"PR #{prNumber} not found on {slug}.",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch);

        if (!string.Equals(poll.HeadRefName, headBranch, StringComparison.Ordinal))
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "pr_identity_mismatch",
                $"PR #{prNumber} head ref is '{poll.HeadRefName ?? "<null>"}' but the verb expected '{headBranch}'.",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch,
                oldPrUrl: $"https://github.com/{slug}/pull/{prNumber}");

        if (!string.Equals(poll.BaseRefName, parentPlanBranch, StringComparison.Ordinal))
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "pr_identity_mismatch",
                $"PR #{prNumber} base ref is '{poll.BaseRefName ?? "<null>"}' but the verb expected '{parentPlanBranch}'.",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch,
                oldPrUrl: $"https://github.com/{slug}/pull/{prNumber}");

        // The polled SHA is captured purely for ledger bookkeeping. Unlike
        // the rebase verb, we don't need a force-with-lease here — the new
        // branch is created from a fresh parent tip, the old branch is
        // deleted outright.
        var polledSha = poll.HeadRefOid ?? string.Empty;
        var oldPrUrl = $"https://github.com/{slug}/pull/{prNumber}";

        // ── 8. Compute desired ancestor snapshot. ──────────────────────────
        var snapshotInBody = PlanPrFrontMatter.Parse(poll.Body).AncestorPlanGenerations;
        var ancestorKeys = ParseAncestorKeysForRecreate(ancestorIds, snapshotInBody);
        var desiredSnapshot = ProjectManifestOntoAncestorsForRecreate(manifest.PlanGenerations, ancestorKeys, snapshotInBody);

        // ── 9. Cascade-precondition: parent plan branch must be fresh. ─────
        var parentFreshness = await CheckParentFreshnessForRecreateAsync(slug, parentPlanBranch, manifest, ct).ConfigureAwait(false);
        if (parentFreshness is { } parentMessage)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "parent_stale", parentMessage,
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch,
                oldPrUrl: oldPrUrl);
        }

        // ── 10. Three-fact noop check. ─────────────────────────────────────
        // Replay-safe: a re-run after a successful recreate should be a noop
        // when (a) old PR is already CLOSED, (b) a fresh OPEN PR exists on
        // the same head with current snapshot, (c) ledger has an entry.
        var existingFresh = await TryFindFreshReplacementPrAsync(slug, headBranch, parentPlanBranch, desiredSnapshot, ct).ConfigureAwait(false);
        bool oldPrAlreadyClosed = !string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase);
        bool ledgerHasEntry = manifest.Rebases.Any(r =>
            string.Equals(r.Branch, headBranch, StringComparison.Ordinal)
            && string.Equals(r.Reason, "child_plan_drift", StringComparison.Ordinal));

        if (oldPrAlreadyClosed && existingFresh is not null && ledgerHasEntry)
        {
            EmitRecreate(new PlanRecreateStaleDescendantResult
            {
                RootId = rootId,
                ItemId = itemId,
                ParentItemId = parentItemId,
                OldPrNumber = prNumber,
                OldPrUrl = oldPrUrl,
                OldHeadBranch = headBranch,
                ParentPlanBranch = parentPlanBranch,
                Outcome = "noop",
                NewPrNumber = existingFresh.Value.Number,
                NewPrUrl = existingFresh.Value.Url,
                NewHeadBranch = headBranch,
                OldPrClosed = true,
            });
            return ExitCodes.Success;
        }

        // After noop check has had its say, an OPEN old PR is the only
        // legitimate input. CLOSED / MERGED PRs are surfaced explicitly.
        if (!string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "pr_state_invalid",
                $"PR #{prNumber} is in state '{poll.State}'; only OPEN PRs are eligible for cascade recreate (and the noop replay condition was not satisfied).",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch,
                oldPrUrl: oldPrUrl);
        }

        // ── 11. Close the old PR. ──────────────────────────────────────────
        var closeComment = "🔄 Recreating after ancestor `plan_generation` bumped. A fresh PR will be opened from the current parent-plan tip; this PR is being closed because its head branch is being replaced.";
        bool closed;
        try
        {
            closed = await gh.ClosePullRequestAsync(slug, prNumber, closeComment, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "pr_close_failed",
                $"gh pr close failed: {ex.Message}",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch, oldPrUrl: oldPrUrl);
        }
        if (!closed)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "pr_close_failed",
                $"gh pr close returned non-success on PR #{prNumber}; refusing to delete the head branch or open a replacement until the close is reconciled.",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch, oldPrUrl: oldPrUrl);
        }

        // ── 12. Delete old head branch (best-effort). ──────────────────────
        var warnings = new List<string>();
        bool oldBranchDeleted = false;
        try
        {
            var deleteResult = await git.DeleteRemoteBranchAsync("origin", headBranch, ct).ConfigureAwait(false);
            if (deleteResult.Succeeded)
            {
                oldBranchDeleted = true;
            }
            else
            {
                warnings.Add($"Old branch delete failed (exit {deleteResult.ExitCode}); branch may have already been removed. Detail: {deleteResult.Stderr.Trim()}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            warnings.Add($"Old branch delete threw: {ex.Message}; continuing.");
        }

        // ── 13. Re-create the plan branch from current parent tip. ─────────
        // We inline the branch creation rather than call EnsurePlan because
        // (a) EnsurePlan emits its own JSON envelope to stdout, polluting
        // this verb's single-result contract; (b) the noop / partial-replay
        // semantics differ — we want to start from a known-fresh parent
        // tip, not an arbitrary local checkout state.
        try
        {
            await git.CheckoutTrackingAsync(parentPlanBranch, "origin", ct).ConfigureAwait(false);
            await git.CreateBranchAsync(headBranch, parentPlanBranch, ct).ConfigureAwait(false);
            await git.PushAsync(headBranch, "origin", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "branch_create_failed",
                $"Failed to re-create plan branch '{headBranch}' from '{parentPlanBranch}': {ex.Message}. Old PR was closed; old branch delete state: {(oldBranchDeleted ? "deleted" : "best-effort failed (see warnings)")}. Replay the verb to complete recovery.",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch, oldPrUrl: oldPrUrl,
                oldPrClosed: true, oldBranchDeleted: oldBranchDeleted, warnings: warnings);
        }

        // ── 14. Open the new PR. ───────────────────────────────────────────
        var prTitle = string.IsNullOrWhiteSpace(poll.HeadRefName)
            ? $"plan: #{itemId} (re-created)"
            : $"plan: #{itemId} (re-created)";
        var bodySummary = BuildRecreateBodySummary(rootId, itemId, headBranch, parentPlanBranch, prNumber, polledSha);
        var fullBody = BuildRecreateBody(desiredSnapshot, bodySummary);

        string? newPrUrl;
        try
        {
            newPrUrl = await gh.CreatePullRequestAsync(slug, parentPlanBranch, headBranch, prTitle, fullBody, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "pr_open_failed",
                $"gh pr create failed: {ex.Message}. Old PR closed + branch re-created; manifest NOT recorded — replay the verb to complete recovery.",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch, oldPrUrl: oldPrUrl,
                oldPrClosed: true, oldBranchDeleted: oldBranchDeleted, newBranchCreated: true, warnings: warnings,
                newHeadBranch: headBranch);
        }
        if (string.IsNullOrWhiteSpace(newPrUrl))
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "pr_open_failed",
                "gh pr create returned no URL; manifest NOT recorded — replay the verb to complete recovery.",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch, oldPrUrl: oldPrUrl,
                oldPrClosed: true, oldBranchDeleted: oldBranchDeleted, newBranchCreated: true, warnings: warnings,
                newHeadBranch: headBranch);
        }

        var trimmedNewUrl = newPrUrl.Trim();
        var newPrNumber = ExtractPrNumberForRecreate(trimmedNewUrl);

        // ── 15. Reload manifest from origin tip + apply ledger + push. ─────
        // Mirrors the rebase verb's reload-after-reset pattern: the manifest
        // we read at step 6 may have been superseded by a peer; re-reading
        // after `reset --hard origin/{feature}` lets us record on top of
        // whatever the current tip is. Apply is idempotent on
        // (branch, commit, reason), so a peer that recorded the SAME entry
        // gives DuplicateSkipped → no commit/push needed.
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
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "internal_error",
                $"Could not checkout/reset {manifestBranch}: {ex.Message}",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch, oldPrUrl: oldPrUrl,
                oldPrClosed: true, oldBranchDeleted: oldBranchDeleted, newBranchCreated: true,
                newPrOpened: true, newPrNumber: newPrNumber, newPrUrl: trimmedNewUrl, newHeadBranch: headBranch,
                warnings: warnings);
        }

        RunManifest manifestForLedger;
        try
        {
            manifestForLedger = RunManifestStore.LoadOrThrow(manifestPath);
        }
        catch (Exception ex)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "manifest_read_failed",
                $"Could not reload manifest at '{manifestPath}' after reset: {ex.Message}",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch, oldPrUrl: oldPrUrl,
                oldPrClosed: true, oldBranchDeleted: oldBranchDeleted, newBranchCreated: true,
                newPrOpened: true, newPrNumber: newPrNumber, newPrUrl: trimmedNewUrl, newHeadBranch: headBranch,
                warnings: warnings);
        }

        // The ledger commit SHA records the OLD PR's head (the head we
        // closed), since that is the (branch, commit) pair that motivated
        // the remedy. Without a polled SHA we fall back to a synthetic
        // marker so Apply still has something non-empty to dedup on.
        var ledgerCommit = string.IsNullOrEmpty(polledSha) ? "recreated" : polledSha;
        var ledgerOutcome = ManifestRebaseLedger.Apply(manifestForLedger, headBranch, ledgerCommit, "child_plan_drift", DateTime.UtcNow);
        if (ledgerOutcome is RebaseLedgerOutcome.Appended appended)
        {
            // Patch the parent refname (Apply leaves Onto empty — same
            // pattern as the rebase verb).
            appended.Record.Onto = $"refs/heads/{parentPlanBranch}";
            manifestRecorded = true;
        }
        else if (ledgerOutcome is RebaseLedgerOutcome.InvalidReason)
        {
            return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "internal_error",
                "ManifestRebaseLedger rejected reason 'child_plan_drift'; this is a bug.",
                oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch, oldPrUrl: oldPrUrl,
                oldPrClosed: true, oldBranchDeleted: oldBranchDeleted, newBranchCreated: true,
                newPrOpened: true, newPrNumber: newPrNumber, newPrUrl: trimmedNewUrl, newHeadBranch: headBranch,
                warnings: warnings);
        }
        // DuplicateSkipped → noop on the ledger; treat as already-pushed.

        if (manifestRecorded)
        {
            try
            {
                RunManifestStore.Save(manifestPath, manifestForLedger);
                await git.StageAsync(manifestPath, ct).ConfigureAwait(false);
                await git.CommitAsync(
                    $"chore(manifest): record recreate of {headBranch} after ancestor plan_generation drift",
                    ct).ConfigureAwait(false);
                await git.PushAsync(manifestBranch, "origin", ct).ConfigureAwait(false);
                manifestPushed = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (ExternalToolException pushEx) when (LooksLikePushRejectForRecreate(pushEx.Stderr))
            {
                try { await git.ResetHardAsync($"origin/{manifestBranch}", ct).ConfigureAwait(false); }
                catch { /* best-effort rollback */ }
                return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "manifest_push_rejected",
                    $"Manifest push to origin/{manifestBranch} rejected (likely a concurrent push). Re-run the verb to complete recovery — the rebase ledger entry will be re-applied idempotently. Detail: {pushEx.Stderr}",
                    oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch, oldPrUrl: oldPrUrl,
                    oldPrClosed: true, oldBranchDeleted: oldBranchDeleted, newBranchCreated: true,
                    newPrOpened: true, newPrNumber: newPrNumber, newPrUrl: trimmedNewUrl, newHeadBranch: headBranch,
                    warnings: warnings);
            }
            catch (Exception ex)
            {
                return EmitRecreateError(rootId, itemId, parentItemId, prNumber, "internal_error",
                    $"Manifest commit/push failed: {ex.Message}",
                    oldHeadBranch: headBranch, parentPlanBranch: parentPlanBranch, oldPrUrl: oldPrUrl,
                    oldPrClosed: true, oldBranchDeleted: oldBranchDeleted, newBranchCreated: true,
                    newPrOpened: true, newPrNumber: newPrNumber, newPrUrl: trimmedNewUrl, newHeadBranch: headBranch,
                    warnings: warnings);
            }
        }

        EmitRecreate(new PlanRecreateStaleDescendantResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            OldPrNumber = prNumber,
            OldPrUrl = oldPrUrl,
            OldHeadBranch = headBranch,
            ParentPlanBranch = parentPlanBranch,
            Outcome = "recreated",
            NewPrNumber = newPrNumber,
            NewPrUrl = trimmedNewUrl,
            NewHeadBranch = headBranch,
            OldPrClosed = true,
            OldBranchDeleted = oldBranchDeleted,
            NewBranchCreated = true,
            NewPrOpened = true,
            ManifestRecorded = manifestRecorded,
            ManifestPushed = manifestPushed,
            Warnings = warnings,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Look for an open replacement PR on <paramref name="headBranch"/>
    /// whose embedded snapshot already matches <paramref name="desiredSnapshot"/>.
    /// Returns the (number, url) of the matching PR or null when no match
    /// (different head, different base, no body, snapshot mismatch).
    /// Best-effort — failure to query gh returns null (treated as "no
    /// replacement found", which falls through to the recreate path).
    /// </summary>
    private async Task<(int Number, string Url)?> TryFindFreshReplacementPrAsync(
        string slug,
        string headBranch,
        string parentPlanBranch,
        IReadOnlyDictionary<string, int> desiredSnapshot,
        CancellationToken ct)
    {
        IReadOnlyList<PullRequestSummary> candidates;
        try
        {
            candidates = await gh.ListPullRequestsAsync(
                slug,
                new PrListFilters(Head: headBranch, Base: parentPlanBranch, State: "open", Limit: 5),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }

        if (candidates.Count == 0) return null;

        foreach (var pr in candidates.OrderByDescending(p => p.Number))
        {
            GhPullRequestPollData? candidatePoll;
            try
            {
                candidatePoll = await gh.GetPullRequestPollDataAsync(slug, pr.Number, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                continue;
            }
            if (candidatePoll is null) continue;
            if (!string.Equals(candidatePoll.State, "OPEN", StringComparison.OrdinalIgnoreCase)) continue;

            var snapshot = PlanPrFrontMatter.Parse(candidatePoll.Body).AncestorPlanGenerations;
            if (SnapshotsEquivalentForRecreate(snapshot, desiredSnapshot))
            {
                return (pr.Number, pr.Url ?? string.Empty);
            }
        }

        return null;
    }

    /// <summary>
    /// Mirrors <see cref="CheckParentFreshnessAsync"/> on the rebase verb,
    /// but with a recreate-flavored message. Returns null when fresh,
    /// a human-readable reason when stale.
    /// </summary>
    private async Task<string?> CheckParentFreshnessForRecreateAsync(
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
            return null;
        }

        if (parentPrs.Count == 0) return null;

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

    private async Task<string?> SafeResolveRepoRootForRecreateAsync(RunLockPathResolver resolver, CancellationToken ct)
    {
        try { return await resolver.ResolveRepoRootAsync(ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private static IReadOnlyList<string> ParseAncestorKeysForRecreate(
        string ancestorIds,
        IReadOnlyDictionary<string, int> existingSnapshot)
    {
        if (!string.IsNullOrWhiteSpace(ancestorIds))
        {
            var parts = ancestorIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) return parts;
        }
        return existingSnapshot.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyDictionary<string, int> ProjectManifestOntoAncestorsForRecreate(
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
                projected[key] = existing;
            }
            else
            {
                projected[key] = 0;
            }
        }
        return projected;
    }

    private static bool SnapshotsEquivalentForRecreate(
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

    private static string BuildRecreateBody(
        IReadOnlyDictionary<string, int> snapshot,
        string summary)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("requests_parent_change: false\n");
        if (snapshot.Count == 0)
        {
            sb.Append("ancestor_plan_generations: {}\n");
        }
        else
        {
            sb.Append("ancestor_plan_generations:\n");
            foreach (var key in snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var displayKey = string.Equals(key, "root", StringComparison.Ordinal) ? key : $"\"{key}\"";
                sb.Append("  ").Append(displayKey).Append(": ").Append(snapshot[key].ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        sb.Append("---\n\n");
        sb.Append(summary);
        if (!summary.EndsWith('\n')) sb.Append('\n');
        return sb.ToString();
    }

    private static string BuildRecreateBodySummary(
        int rootId,
        int itemId,
        string headBranch,
        string parentPlanBranch,
        int oldPrNumber,
        string oldHeadSha)
    {
        var sb = new StringBuilder();
        sb.Append($"## Plan for #{itemId} (root #{rootId}) — re-created after ancestor `plan_generation` bumped\n\n");
        sb.Append("Promotes `").Append(headBranch).Append("` into `").Append(parentPlanBranch).Append("`.\n\n");
        sb.Append($"Replaces #{oldPrNumber}");
        if (!string.IsNullOrEmpty(oldHeadSha))
        {
            sb.Append($" (old head `{ShortShaForRecreate(oldHeadSha)}`)");
        }
        sb.Append(". The previous head branch was deleted; this PR is opened from the current parent-plan tip with an up-to-date `ancestor_plan_generations` snapshot.\n\n");
        sb.Append("AB#").Append(itemId).Append('\n');
        return sb.ToString();
    }

    private static string ShortShaForRecreate(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    private static int ExtractPrNumberForRecreate(string url)
    {
        var match = PrUrlNumberRegex.Match(url);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
    }

    private static bool LooksLikePushRejectForRecreate(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase);
    }

    private static int EmitRecreateError(
        int rootId,
        int itemId,
        int parentItemId,
        int prNumber,
        string errorCode,
        string message,
        string oldHeadBranch = "",
        string parentPlanBranch = "",
        string oldPrUrl = "",
        bool oldPrClosed = false,
        bool oldBranchDeleted = false,
        bool newBranchCreated = false,
        bool newPrOpened = false,
        int? newPrNumber = null,
        string? newPrUrl = null,
        string? newHeadBranch = null,
        IReadOnlyList<string>? warnings = null)
    {
        EmitRecreate(new PlanRecreateStaleDescendantResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            OldPrNumber = prNumber,
            OldPrUrl = oldPrUrl,
            OldHeadBranch = oldHeadBranch,
            ParentPlanBranch = parentPlanBranch,
            Outcome = errorCode,
            NewPrNumber = newPrNumber,
            NewPrUrl = newPrUrl,
            NewHeadBranch = newHeadBranch,
            OldPrClosed = oldPrClosed,
            OldBranchDeleted = oldBranchDeleted,
            NewBranchCreated = newBranchCreated,
            NewPrOpened = newPrOpened,
            Warnings = warnings ?? [],
            Error = message,
            ErrorCode = errorCode,
        });
        return ExitCodes.Success;
    }

    private static void EmitRecreate(PlanRecreateStaleDescendantResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PlanRecreateStaleDescendantResult));
}
