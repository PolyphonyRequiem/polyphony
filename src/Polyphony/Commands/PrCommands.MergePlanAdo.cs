using System.Globalization;
using System.Net;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;
using Polyphony.Locking;
using Polyphony.Manifest;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Azure DevOps analogue of <c>polyphony pr merge-plan-pr</c>. Merges
    /// a plan PR (head = <c>plan/{root}-{item_id}</c> or
    /// <c>plan/{root}</c>) into its parent plan branch (or the feature
    /// branch for the root plan), then records the merge in the run
    /// manifest's <c>merged_plan_prs</c> ledger and pushes the manifest
    /// mutation to <c>feature/{root}</c>. The whole sequence runs under
    /// the same-root run lock at
    /// <c>&lt;repoRoot&gt;/.polyphony/locks/run-{rootId}.lock</c>.
    ///
    /// <para>Mirrors the GitHub-side verb step-for-step: lock-before-merge,
    /// pre-merge poll + identity validation, P6 stale-generation refusal,
    /// branch on PR state, manifest checkout/reset, ledger application via
    /// the shared <see cref="ManifestPlanLedger"/>, and push with
    /// rejection-rollback. The platform call is
    /// <see cref="IAdoClient.CompletePullRequestAsync"/> which
    /// (per ADR Rev 4) pins the merge strategy to <c>noFastForward</c> and
    /// supplies the polled head SHA as <c>lastMergeSourceCommit.commitId</c>
    /// — the ADO analogue of GitHub's <c>--match-head-commit</c>
    /// stale-head guard.</para>
    ///
    /// <para><b>Diff validation deferred for v1.</b> The GitHub-side P8b
    /// guard uses <c>gh.GetPullRequestFilesAsync</c>; the ADO equivalent is
    /// not yet exposed on <see cref="IAdoClient"/>, so this verb skips the
    /// in-line plan-diff classification. The standalone advisory verb
    /// <c>polyphony pr validate-plan-diff</c> remains the platform-agnostic
    /// review-time check.</para>
    ///
    /// <para><b>Routing-style exit code.</b> Always exits 0 on outcomes
    /// the workflow can route on (lock held, push rejected, identity
    /// mismatch, stale head, etc.) — consumers branch on
    /// <see cref="PrMergePlanAdoResult.ErrorCode"/>. Exits non-zero only
    /// for genuinely unexpected exceptions (with <c>internal_error</c>).</para>
    /// </summary>
    /// <param name="organization">ADO organization name (e.g. <c>contoso</c>).</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name; both accepted.</param>
    /// <param name="rootId">Run's root work-item id (positive).</param>
    /// <param name="itemId">Plan-owning work-item id; equal to <paramref name="rootId"/> for the root plan.</param>
    /// <param name="prNumber">PR number to merge (positive).</param>
    /// <param name="parentItemId">Immediate plan-tree parent's id; required for descendants of descendants. Omit for root plan and direct children of root plan.</param>
    /// <param name="manifestPath">Path to the run manifest. Defaults to <c>.polyphony/run.yaml</c>.</param>
    /// <param name="lockTtlHours">Run-lock TTL (default 24).</param>
    /// <param name="by">Lock acquirer name; defaults to <c>USERNAME</c>/<c>USER</c> env.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("merge-plan-ado")]
    [VerbResult(typeof(PrMergePlanAdoResult))]
    public async Task<int> MergePlanAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        int prNumber = RequiredInput.MissingInt,
        int parentItemId = 0,
        string manifestPath = "",
        int lockTtlHours = 24,
        string by = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr merge-plan-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt),
            ("--pr-number", prNumber == RequiredInput.MissingInt)) is { } halt)
            return halt;
        var slug = BuildAdoSlug(organization, project, repository);
        var prUrl = BuildAdoPrUrl(organization, project, repository, prNumber);

        // ── 1. Validate inputs + derive head/base. ──────────────────────────
        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository))
        {
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "invalid_argument", "organization, project, and repository are required");
        }
        if (!Branching.RootId.TryParse(rootId, out var root))
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "invalid_argument", $"--root-id must be positive (got {rootId})");

        if (!WorkItemId.TryParse(itemId, out var item))
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "invalid_argument", $"--item-id must be positive (got {itemId})");

        if (prNumber <= 0)
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "invalid_argument", $"--pr-number must be positive (got {prNumber})");

        bool isRootPlan = itemId == rootId;
        string itemKey;
        string headBranch;
        string baseBranch;
        int resolvedParent = 0;

        if (isRootPlan)
        {
            if (parentItemId != 0)
                return EmitMergePlanAdoError(
                    rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                    "invalid_argument",
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
                    return EmitMergePlanAdoError(
                        rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                        "invalid_argument", $"--parent-item-id must be positive (got {parentItemId})");
                if (parentItemId == itemId)
                    return EmitMergePlanAdoError(
                        rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                        "invalid_argument",
                        $"--parent-item-id ({parentItemId}) must not equal --item-id; a plan cannot be its own parent.");
                if (parentItemId == rootId)
                    return EmitMergePlanAdoError(
                        rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                        "invalid_argument",
                        $"--parent-item-id ({parentItemId}) equals --root-id; omit --parent-item-id when the parent is the root plan.");
                resolvedParent = parentItemId;
                headBranch = BranchNameBuilder.DescendantPlan(root, item).Value;
                baseBranch = BranchNameBuilder.DescendantPlan(root, parentItem).Value;
            }
            itemKey = itemId.ToString(CultureInfo.InvariantCulture);
        }

        var manifestBranch = BranchNameBuilder.Feature(root).Value;

        // ── 1b. Resolve local manifest path (Rev 4.2). ─────────────────────
        var resolvedPath = await ManifestPathHelper.ResolveAsync(statePaths, rootId, manifestPath, ct).ConfigureAwait(false);
        if (resolvedPath.Error is not null)
            return EmitMergePlanAdoError(
                rootId, itemId, resolvedParent, organization, project, repository, slug, prUrl, prNumber,
                "internal_error", resolvedPath.Error,
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch);
        var localManifestPath = resolvedPath.Path;

        if (ado is null)
        {
            return EmitMergePlanAdoError(
                rootId, itemId, resolvedParent, organization, project, repository, slug, prUrl, prNumber,
                "ado_failed", "IAdoClient is not configured",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch);
        }

        // ── 2. Acquire run lock. ───────────────────────────────────────────
        string lockPath;
        try
        {
            lockPath = await lockPathResolver.ResolveAsync(rootId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return EmitMergePlanAdoError(
                rootId, itemId, resolvedParent, organization, project, repository, slug, prUrl, prNumber,
                "internal_error", ex.Message,
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch);
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
            return EmitMergePlanAdoError(
                rootId, itemId, resolvedParent, organization, project, repository, slug, prUrl, prNumber,
                code,
                $"Could not acquire run lock at '{lockPath}' (reason: {acquireOutcome.Reason?.ToString().ToLowerInvariant() ?? "unknown"}).",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken);
        }

        try
        {
            return await MergePlanAdoUnderLockAsync(
                rootId, itemId, resolvedParent, organization, project, repository, slug, prUrl, prNumber,
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, localManifestPath,
                lockToken, ct).ConfigureAwait(false);
        }
        finally
        {
            // Best-effort release; mirrors the GitHub-side verb. LockReleased on
            // the result is set via the surrounding emit; we re-emit a warning
            // on stderr if release failed so workflows can route on a leaked lock.
            bool released = false;
            try
            {
                var release = lockStore.TryRelease(lockPath, lockToken);
                released = release.Released;
            }
            catch
            {
                released = false;
            }

            if (!released)
            {
                Console.Error.WriteLine(
                    $"WARNING: failed to release run lock at '{lockPath}' (token={lockToken}); run `polyphony lock force-release --root-id {rootId}` if needed.");
            }
        }
    }

    private async Task<int> MergePlanAdoUnderLockAsync(
        int rootId,
        int itemId,
        int parentItemId,
        string organization,
        string project,
        string repository,
        string slug,
        string prUrl,
        int prNumber,
        bool isRootPlan,
        string itemKey,
        string headBranch,
        string baseBranch,
        string manifestBranch,
        string manifestPath,
        string lockToken,
        CancellationToken ct)
    {
        // ── 3. Verify clean worktree. ──────────────────────────────────────
        try
        {
            var status = await git.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.Count > 0)
                return EmitMergePlanAdoError(
                    rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                    "worktree_dirty",
                    $"Worktree is not clean ({status.Count} entries from `git status --porcelain`); commit, stash, or discard local changes before retrying.",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "internal_error",
                $"Could not read worktree status: {ex.Message}",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken);
        }

        // ── 4. Skipped (Rev 4.2): no need to fetch the feature branch.
        // The manifest is local + canonical; the post-merge sequence below
        // does not need worktree state.

        // ── 5. Poll PR + validate identity. ────────────────────────────────
        AdoPullRequestPollData? poll;
        try
        {
            poll = await ado!.GetPullRequestPollDataAsync(
                organization, project, repository, prNumber, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "no_pat", ex.Message,
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken);
        }
        catch (TimeoutException ex)
        {
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "ado_timeout", ex.Message,
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken);
        }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                code, ex.Message,
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken);
        }
        catch (Exception ex)
        {
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "ado_failed",
                $"ADO PR poll failed: {ex.Message}",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken);
        }
        if (poll is null)
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "pr_not_found",
                $"PR #{prNumber} not found in {slug}.",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken);

        if (!string.Equals(poll.HeadRefName, headBranch, StringComparison.Ordinal))
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "pr_identity_mismatch",
                $"PR #{prNumber} head ref is '{poll.HeadRefName}' but the verb expected '{headBranch}'. Refusing to act on the wrong PR.",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                prState: poll.State);

        if (!string.Equals(poll.BaseRefName, baseBranch, StringComparison.Ordinal))
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "pr_identity_mismatch",
                $"PR #{prNumber} base ref is '{poll.BaseRefName}' but the verb expected '{baseBranch}'. Refusing to act on the wrong PR.",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                prState: poll.State);

        // ── 5b. Stale-generation refusal (P6). Same logic as the GitHub-side
        // verb — skipped for root plans (no ancestors) and for MERGED PRs
        // (the merge already happened; we're in recovery mode).
        if (string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase) && !isRootPlan)
        {
            var snapshot = PlanPrFrontMatter.Parse(poll.Body).AncestorPlanGenerations;

            // Read manifest from local disk (Rev 4.2). Missing file = no
            // staleness signal → fall through.
            RunManifest? localManifest = null;
            if (File.Exists(manifestPath))
            {
                try
                {
                    localManifest = RunManifestStore.LoadOrThrow(manifestPath);
                }
                catch (Exception ex)
                {
                    return EmitMergePlanAdoError(
                        rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                        "internal_error",
                        $"Could not parse manifest at {manifestPath} for staleness check: {ex.Message}",
                        isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                        prState: poll.State);
                }
            }

            if (localManifest is not null)
            {
                var currentGens = localManifest.PlanGenerations;
                var staleness = PlanGenerationStaleness.Check(snapshot, currentGens);
                if (staleness.IsEmpty)
                {
                    return EmitMergePlanAdoError(
                        rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                        "stale_generation",
                        $"PR #{prNumber} body has no ancestor_plan_generations snapshot in front-matter; descendant plan PRs must carry a snapshot to be merged safely. Re-open the PR via `polyphony pr open-plan-ado` to embed the current snapshot.",
                        isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
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

                    return EmitMergePlanAdoError(
                        rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                        "stale_generation",
                        $"PR #{prNumber} ancestor plan-generation snapshot is stale vs the current manifest on origin/{manifestBranch}. Stale entries: {PlanGenerationStaleness.FormatStaleEntries(staleness.StaleEntries)}. Re-open the PR with the current snapshot before merging.",
                        isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                        prState: poll.State, staleAncestors: staleEntries);
                }
            }
        }

        // ── 6. Branch on PR state. ─────────────────────────────────────────
        // P8b plan-diff validation (the GitHub-side guard) is deferred —
        // IAdoClient does not yet expose a changed-files endpoint. The
        // standalone `polyphony pr validate-plan-diff` advisory verb remains
        // the platform-agnostic review-time check.
        string mergeCommit;
        bool alreadyMerged;
        if (string.Equals(poll.State, "MERGED", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(poll.MergeCommit))
                return EmitMergePlanAdoError(
                    rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                    "missing_merge_commit",
                    $"PR #{prNumber} reports state MERGED but ADO did not return a merge commit SHA.",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                    prState: poll.State);
            mergeCommit = poll.MergeCommit;
            alreadyMerged = true;
        }
        else if (string.Equals(poll.State, "OPEN", StringComparison.OrdinalIgnoreCase))
        {
            AdoCompletePullRequestResult complete;
            try
            {
                complete = await ado!.CompletePullRequestAsync(
                    organization, project, repository, prNumber,
                    lastMergeSourceCommitSha: poll.HeadRefOid,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex)
            {
                return EmitMergePlanAdoError(
                    rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                    "no_pat", ex.Message,
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                    prState: poll.State);
            }
            catch (TimeoutException ex)
            {
                return EmitMergePlanAdoError(
                    rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                    "ado_timeout", ex.Message,
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                    prState: poll.State);
            }
            catch (HttpRequestException ex)
            {
                var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "no_pat"
                    : "ado_complete_failed";
                return EmitMergePlanAdoError(
                    rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                    code, ex.Message,
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                    prState: poll.State);
            }
            catch (Exception ex)
            {
                return EmitMergePlanAdoError(
                    rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                    "ado_complete_failed",
                    $"ADO complete-PR call failed: {ex.Message}",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                    prState: poll.State);
            }

            switch (complete.Status)
            {
                case "completed":
                    if (string.IsNullOrEmpty(complete.MergeCommitSha))
                        return EmitMergePlanAdoError(
                            rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                            "missing_merge_commit",
                            "ADO complete-PR succeeded but did not return a merge commit SHA; cannot record the merge in the ledger.",
                            isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                            prState: "MERGED");
                    mergeCommit = complete.MergeCommitSha;
                    alreadyMerged = false;
                    break;
                case "stale_head":
                    return EmitMergePlanAdoError(
                        rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                        "stale_head",
                        $"ADO refused to complete PR #{prNumber}: source branch advanced past the polled head SHA '{poll.HeadRefOid}'. Re-poll and retry. Detail: {complete.ErrorBody}",
                        isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                        prState: poll.State);
                case "not_found":
                    return EmitMergePlanAdoError(
                        rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                        "pr_not_found",
                        $"PR #{prNumber} disappeared between poll and complete in {slug}.",
                        isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                        prState: poll.State);
                case "not_mergeable":
                default:
                    return EmitMergePlanAdoError(
                        rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                        "ado_complete_failed",
                        $"ADO refused to complete PR #{prNumber} (HTTP {complete.HttpStatus}, status={complete.Status}): {complete.ErrorBody}",
                        isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                        prState: poll.State);
            }
        }
        else
        {
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "pr_state_invalid",
                $"PR #{prNumber} is in state '{poll.State}'; only OPEN or MERGED are actionable.",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                prState: poll.State);
        }

        // ── 7. Skipped (Rev 4.2): no checkout/reset needed.
        // Manifest is local + canonical; ADO complete-PR landed on origin
        // but does not affect the manifest file (which lives outside the
        // worktree under <git-common-dir>/polyphony/...).

        // ── 8. Apply ledger; save under run lock. ──────────────────────────
        RunManifest manifest;
        try
        {
            manifest = RunManifestStore.LoadOrThrow(manifestPath);
        }
        catch (Exception ex)
        {
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "manifest_read_failed",
                $"Could not load manifest at '{manifestPath}': {ex.Message}",
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                prState: "MERGED", merged: true, alreadyMerged: alreadyMerged, mergeCommit: mergeCommit);
        }

        var ledger = ManifestPlanLedger.Apply(manifest, itemKey, prNumber, mergeCommit, DateTime.UtcNow);
        if (ledger.ConflictReason is not null)
            return EmitMergePlanAdoError(
                rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                "ledger_conflict", ledger.ConflictReason,
                isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                prState: "MERGED", merged: true, alreadyMerged: alreadyMerged, mergeCommit: mergeCommit,
                prevGen: ledger.PreviousGeneration, currGen: ledger.CurrentGeneration);

        bool manifestRecorded = ledger.Recorded;
        bool manifestPushed = false;

        if (manifestRecorded)
        {
            try
            {
                RunManifestStore.Save(manifestPath, manifest);
                manifestPushed = true;
            }
            catch (Exception ex)
            {
                return EmitMergePlanAdoError(
                    rootId, itemId, parentItemId, organization, project, repository, slug, prUrl, prNumber,
                    "internal_error",
                    $"Manifest save failed: {ex.Message}",
                    isRootPlan, itemKey, headBranch, baseBranch, manifestBranch, lockToken: lockToken,
                    prState: "MERGED", merged: true, alreadyMerged: alreadyMerged, mergeCommit: mergeCommit,
                    prevGen: ledger.PreviousGeneration, currGen: ledger.CurrentGeneration,
                    manifestRecorded: false, manifestPushed: false);
            }
        }

        // ── 9. Emit success. ───────────────────────────────────────────────
        EmitMergePlanAdo(new PrMergePlanAdoResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            ItemKey = itemKey,
            IsRootPlan = isRootPlan,
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            ManifestBranch = manifestBranch,
            Organization = organization,
            Project = project,
            Repository = repository,
            RepoSlug = slug,
            PrNumber = prNumber,
            PrUrl = prUrl,
            PrState = "MERGED",
            Merged = true,
            AlreadyMerged = alreadyMerged,
            MergeCommit = mergeCommit,
            ManifestRecorded = manifestRecorded,
            ManifestPushed = manifestPushed,
            PreviousGeneration = ledger.PreviousGeneration,
            CurrentGeneration = ledger.CurrentGeneration,
            LockToken = lockToken,
            LockReleased = false,
            ErrorCode = "",
        });
        return ExitCodes.Success;
    }

    private static void EmitMergePlanAdo(PrMergePlanAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrMergePlanAdoResult));

    private static int EmitMergePlanAdoError(
        int rootId,
        int itemId,
        int parentItemId,
        string organization,
        string project,
        string repository,
        string slug,
        string prUrl,
        int prNumber,
        string errorCode,
        string message,
        bool isRootPlan = false,
        string itemKey = "",
        string headBranch = "",
        string baseBranch = "",
        string manifestBranch = "",
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
        EmitMergePlanAdo(new PrMergePlanAdoResult
        {
            RootId = rootId,
            ItemId = itemId,
            ParentItemId = parentItemId,
            ItemKey = itemKey,
            IsRootPlan = isRootPlan,
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            ManifestBranch = manifestBranch,
            Organization = organization ?? string.Empty,
            Project = project ?? string.Empty,
            Repository = repository ?? string.Empty,
            RepoSlug = slug ?? string.Empty,
            PrNumber = prNumber,
            PrUrl = prUrl ?? string.Empty,
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
}
