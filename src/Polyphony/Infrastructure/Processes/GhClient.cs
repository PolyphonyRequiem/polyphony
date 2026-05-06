using System.Text.Json;
using System.Text.Json.Nodes;

namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Default <see cref="IGhClient"/> backed by <see cref="IProcessRunner"/>.
///
/// Every gh invocation flows through <see cref="RunWithRetryAsync"/>, which
/// applies the configured <see cref="GhClientPolicy"/>: per-attempt timeout
/// (the runner kills the gh process tree on cancellation), retry-on-timeout
/// only, and exponential backoff between retries. Caller-driven
/// cancellation is propagated immediately and never converted into a
/// retryable timeout.
///
/// Mutation operations (<see cref="CreatePullRequestAsync"/>) layer
/// reconciliation on top of the retry policy, because a timed-out
/// <c>gh pr create</c> may still have been accepted server-side — naive
/// retry would create a duplicate or fail with "already exists" while
/// silently masking the real outcome.
/// </summary>
public sealed class GhClient : IGhClient
{
    private const string Exe = "gh";

    private readonly IProcessRunner _runner;
    private readonly GhClientPolicy _policy;

    public GhClient(IProcessRunner runner)
        : this(runner, GhClientPolicy.Default)
    {
    }

    public GhClient(IProcessRunner runner, GhClientPolicy policy)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<GhAuthStatus> GetAuthStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunWithRetryAsync(["auth", "status"], ct).ConfigureAwait(false);
            // gh writes its detail message to stderr regardless of state.
            var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            return new GhAuthStatus(result.Succeeded, detail.Trim());
        }
        catch (ExternalToolTimeoutException ex)
        {
            // Auth probe is best-effort: surface "not authenticated" with the timeout
            // detail so preflight can route to remediation rather than crashing.
            return new GhAuthStatus(false, $"gh auth status timed out after {ex.Attempts} attempt(s)");
        }
    }

    public async Task<IReadOnlyList<PullRequestSummary>> ListPullRequestsAsync(
        string repoSlug,
        PrListFilters filters,
        CancellationToken ct = default)
    {
        var args = BuildPrListArgs(repoSlug, filters);

        // Note: ExternalToolTimeoutException is intentionally NOT caught here.
        // Callers that can tolerate "no info" on a hang already wrap calls in
        // try/catch (see BranchCommands.LoadTree, BranchCommands.Route,
        // StateCommands.Detect). The PR-create gate in PrCommands MUST see
        // a timeout distinctly from "no PRs found" — otherwise it would
        // silently create a duplicate PR.
        var result = await RunWithRetryAsync(args, ct).ConfigureAwait(false);
        return result.Succeeded ? ParsePrList(result.Stdout) : [];
    }

    public async Task<string> CreatePullRequestAsync(
        string repoSlug,
        string baseBranch,
        string headBranch,
        string title,
        string body,
        CancellationToken ct = default)
    {
        string[] args =
        [
            "pr", "create",
            "--repo", repoSlug,
            "--base", baseBranch,
            "--head", headBranch,
            "--title", title,
            "--body", body,
        ];

        // Mutation reconciliation: between attempts (and on the
        // "already exists" stderr path), look up an open PR for the same
        // (head, base) pair. A timed-out attempt may have been accepted
        // server-side; without reconciliation, attempt 2 would either
        // create a duplicate or fail loudly while the real PR sits there.
        for (int attempt = 1; attempt <= _policy.MaxAttempts; attempt++)
        {
            try
            {
                var result = await RunSingleAttemptAsync(args, ct).ConfigureAwait(false);

                if (result.Succeeded)
                {
                    var url = result.Stdout.Trim();
                    if (string.IsNullOrEmpty(url))
                    {
                        throw new ExternalToolException(
                            Exe, args, result.ExitCode, result.Stdout,
                            "gh pr create returned no URL");
                    }
                    return url;
                }

                // Non-zero exit: try to reconcile on "already exists" stderr,
                // otherwise propagate as a real error (no retry on non-zero exit).
                if (LooksLikeAlreadyExists(result.Stderr))
                {
                    var reconciled = await TryReconcileExistingPrAsync(repoSlug, baseBranch, headBranch, ct).ConfigureAwait(false);
                    if (reconciled is not null) return reconciled;
                }

                throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Per-attempt timeout fired. Before giving up or backing off,
                // check whether the timed-out call actually succeeded
                // server-side and the PR is now visible.
                var reconciled = await TryReconcileExistingPrAsync(repoSlug, baseBranch, headBranch, ct).ConfigureAwait(false);
                if (reconciled is not null) return reconciled;

                if (attempt >= _policy.MaxAttempts)
                {
                    throw new ExternalToolTimeoutException(
                        Exe, args, _policy.MaxAttempts, _policy.PerAttemptTimeout);
                }

                await DelayBackoffAsync(attempt, ct).ConfigureAwait(false);
            }
        }

        // Unreachable: the loop always either returns or throws above.
        throw new InvalidOperationException("CreatePullRequestAsync exited the retry loop without returning.");
    }

    public async Task<GhMergeResult> MergePullRequestAsync(
        string repoSlug,
        int prNumber,
        GhMergeMethod method,
        bool admin = false,
        bool deleteBranch = false,
        string? matchHeadCommit = null,
        CancellationToken ct = default)
    {
        var args = BuildPrMergeArgs(repoSlug, prNumber, method, admin, deleteBranch, matchHeadCommit);

        for (int attempt = 1; attempt <= _policy.MaxAttempts; attempt++)
        {
            try
            {
                var result = await RunSingleAttemptAsync(args, ct).ConfigureAwait(false);

                if (result.Succeeded)
                {
                    // gh pr merge stdout is human-oriented and unreliable —
                    // fetch the canonical state to populate the merge SHA.
                    var state = await TryGetStateForReconcileAsync(repoSlug, prNumber, ct).ConfigureAwait(false);
                    return new GhMergeResult(
                        Succeeded: true,
                        PrNumber: prNumber,
                        MergeSha: state?.MergeCommitSha,
                        AlreadyMerged: false,
                        Detail: string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr.Trim() : result.Stdout.Trim());
                }

                // Non-zero exit: reconcile against current PR state. If the
                // server already records the PR as merged, treat as success
                // (idempotent retry). Otherwise propagate as a real error.
                if (LooksLikeAlreadyMerged(result.Stderr))
                {
                    var reconciled = await TryGetStateForReconcileAsync(repoSlug, prNumber, ct).ConfigureAwait(false);
                    if (reconciled is { State: "MERGED" })
                    {
                        return new GhMergeResult(
                            Succeeded: true,
                            PrNumber: prNumber,
                            MergeSha: reconciled.MergeCommitSha,
                            AlreadyMerged: true,
                            Detail: result.Stderr.Trim());
                    }
                }

                throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Per-attempt timeout fired. Before backing off, check whether
                // the merge actually succeeded server-side.
                var reconciled = await TryGetStateForReconcileAsync(repoSlug, prNumber, ct).ConfigureAwait(false);
                if (reconciled is { State: "MERGED" })
                {
                    return new GhMergeResult(
                        Succeeded: true,
                        PrNumber: prNumber,
                        MergeSha: reconciled.MergeCommitSha,
                        AlreadyMerged: true,
                        Detail: $"reconciled after timeout — PR was already merged ({reconciled.MergeCommitSha})");
                }

                if (attempt >= _policy.MaxAttempts)
                {
                    throw new ExternalToolTimeoutException(
                        Exe, args, _policy.MaxAttempts, _policy.PerAttemptTimeout);
                }

                await DelayBackoffAsync(attempt, ct).ConfigureAwait(false);
            }
        }

        // Unreachable: the loop always either returns or throws above.
        throw new InvalidOperationException("MergePullRequestAsync exited the retry loop without returning.");
    }

    public async Task<GhPullRequestState?> GetPullRequestStateAsync(
        string repoSlug,
        int prNumber,
        CancellationToken ct = default)
    {
        string[] args =
        [
            "pr", "view", prNumber.ToString(),
            "--repo", repoSlug,
            "--json", "number,state,mergeCommit,headRefName,headRefOid",
        ];
        var result = await RunWithRetryAsync(args, ct).ConfigureAwait(false);
        return result.Succeeded ? ParsePrState(result.Stdout) : null;
    }

    public async Task<GhPullRequestPollData?> GetPullRequestPollDataAsync(
        string repoSlug,
        int prNumber,
        CancellationToken ct = default)
    {
        string[] args =
        [
            "pr", "view", prNumber.ToString(),
            "--repo", repoSlug,
            "--json", "number,state,reviewDecision,reviews,headRefOid,baseRefName,headRefName,mergeable,mergedAt,mergeCommit,body",
        ];
        var result = await RunWithRetryAsync(args, ct).ConfigureAwait(false);
        return result.Succeeded ? ParsePrPollData(result.Stdout) : null;
    }

    public async Task<IReadOnlyList<GhPullRequestChangedFile>?> GetPullRequestFilesAsync(
        string repoSlug,
        int prNumber,
        CancellationToken ct = default)
    {
        string[] args =
        [
            "pr", "view", prNumber.ToString(),
            "--repo", repoSlug,
            "--json", "files",
        ];
        var result = await RunWithRetryAsync(args, ct).ConfigureAwait(false);
        return result.Succeeded ? ParsePrFiles(result.Stdout) : null;
    }

    public async Task<string?> GetPullRequestDiffAsync(
        string repoSlug,
        int prNumber,
        CancellationToken ct = default)
    {
        string[] args =
        [
            "pr", "diff", prNumber.ToString(),
            "--repo", repoSlug,
        ];
        var result = await RunWithRetryAsync(args, ct).ConfigureAwait(false);
        return result.Succeeded ? result.Stdout : null;
    }

    public async Task<bool> EditPullRequestBodyAsync(
        string repoSlug,
        int prNumber,
        string body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoSlug);
        ArgumentNullException.ThrowIfNull(body);
        if (prNumber <= 0) throw new ArgumentOutOfRangeException(nameof(prNumber), "PR number must be positive.");

        string[] args =
        [
            "pr", "edit", prNumber.ToString(),
            "--repo", repoSlug,
            "--body-file", "-",
        ];

        // Cascade-remedy contract: never throw. Any failure (timeout exhausted,
        // non-zero exit) becomes a routable false. Caller-driven cancellation
        // still escapes as OperationCanceledException — that's not a tool
        // failure, it's the orchestrator pulling the plug.
        try
        {
            var result = await RunWithRetryAsync(args, ct, stdin: body).ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ExternalToolTimeoutException)
        {
            return false;
        }
    }

    public async Task<bool> CommentPullRequestAsync(
        string repoSlug,
        int prNumber,
        string body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoSlug);
        ArgumentNullException.ThrowIfNull(body);
        if (prNumber <= 0) throw new ArgumentOutOfRangeException(nameof(prNumber), "PR number must be positive.");

        string[] args =
        [
            "pr", "comment", prNumber.ToString(),
            "--repo", repoSlug,
            "--body-file", "-",
        ];

        try
        {
            var result = await RunWithRetryAsync(args, ct, stdin: body).ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ExternalToolTimeoutException)
        {
            return false;
        }
    }

    public async Task<bool> ClosePullRequestAsync(
        string repoSlug,
        int prNumber,
        string commentBeforeClose,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoSlug);
        if (prNumber <= 0) throw new ArgumentOutOfRangeException(nameof(prNumber), "PR number must be positive.");

        // commentBeforeClose is optional — null/empty omits the --comment flag entirely.
        // Mirrors the cascade-remedy contract of the sibling mutators
        // (EditPullRequestBodyAsync / CommentPullRequestAsync): never throw on tool
        // failure; surface a routable bool so the verb can react.
        var args = string.IsNullOrEmpty(commentBeforeClose)
            ? (string[])["pr", "close", prNumber.ToString(), "--repo", repoSlug]
            : (string[])["pr", "close", prNumber.ToString(), "--repo", repoSlug, "--comment", commentBeforeClose];

        try
        {
            var result = await RunWithRetryAsync(args, ct).ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ExternalToolTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Run an external command with the configured retry-on-timeout policy.
    /// Returns whatever <see cref="ProcessResult"/> the runner produced; the
    /// caller decides how to interpret exit codes. Throws
    /// <see cref="ExternalToolTimeoutException"/> if every attempt timed out.
    /// </summary>
    private async Task<ProcessResult> RunWithRetryAsync(
        IReadOnlyList<string> args,
        CancellationToken ct,
        string? stdin = null)
    {
        for (int attempt = 1; attempt <= _policy.MaxAttempts; attempt++)
        {
            try
            {
                return await RunSingleAttemptAsync(args, ct, stdin).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancelled — propagate immediately, never retry.
                throw;
            }
            catch (OperationCanceledException)
            {
                // Per-attempt timeout fired.
                if (attempt >= _policy.MaxAttempts)
                {
                    throw new ExternalToolTimeoutException(
                        Exe, args, _policy.MaxAttempts, _policy.PerAttemptTimeout);
                }
                await DelayBackoffAsync(attempt, ct).ConfigureAwait(false);
            }
        }

        // Unreachable: the loop always either returns or throws above.
        throw new InvalidOperationException("RunWithRetryAsync exited the retry loop without returning.");
    }

    private async Task<ProcessResult> RunSingleAttemptAsync(
        IReadOnlyList<string> args,
        CancellationToken ct,
        string? stdin = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_policy.PerAttemptTimeout);
        return await _runner.RunAsync(Exe, args, timeoutCts.Token, stdin: stdin).ConfigureAwait(false);
    }

    private async Task DelayBackoffAsync(int attemptJustFailed, CancellationToken ct)
    {
        if (_policy.InitialBackoff <= TimeSpan.Zero) return;
        // 1s, 2s, 4s, ... — no jitter (single-process CLI).
        var multiplier = 1L << (attemptJustFailed - 1);
        var delay = TimeSpan.FromTicks(_policy.InitialBackoff.Ticks * multiplier);
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private async Task<string?> TryReconcileExistingPrAsync(
        string repoSlug, string baseBranch, string headBranch, CancellationToken ct)
    {
        try
        {
            var existing = await ListPullRequestsAsync(
                repoSlug,
                new PrListFilters(Head: headBranch, Base: baseBranch, State: "open", Limit: 1),
                ct).ConfigureAwait(false);
            return existing.Count > 0 ? existing[0].Url : null;
        }
        catch
        {
            // Reconciliation is best-effort — if list also fails, fall through
            // to the original timeout/error path and let the caller see it.
            return null;
        }
    }

    private static bool LooksLikeAlreadyExists(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        // gh emits messages like:
        //   "a pull request for branch X into branch Y already exists: <url>"
        return stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAlreadyMerged(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        // gh emits messages like:
        //   "Pull request #N is in clean status"  (no — different case)
        //   "the pull request is not mergeable: pull request is already merged"
        //   "this pull request is already merged"
        return stderr.Contains("already merged", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GhPullRequestState?> TryGetStateForReconcileAsync(
        string repoSlug, int prNumber, CancellationToken ct)
    {
        try
        {
            return await GetPullRequestStateAsync(repoSlug, prNumber, ct).ConfigureAwait(false);
        }
        catch
        {
            // Reconciliation is best-effort — if view also fails, fall through
            // to the original timeout/error path and let the caller see it.
            return null;
        }
    }

    private static string[] BuildPrMergeArgs(
        string repoSlug,
        int prNumber,
        GhMergeMethod method,
        bool admin,
        bool deleteBranch,
        string? matchHeadCommit)
    {
        var args = new List<string>
        {
            "pr", "merge", prNumber.ToString(),
            "--repo", repoSlug,
            method switch
            {
                GhMergeMethod.Merge => "--merge",
                GhMergeMethod.Squash => "--squash",
                GhMergeMethod.Rebase => "--rebase",
                _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown merge method"),
            },
        };
        if (admin) args.Add("--admin");
        if (deleteBranch) args.Add("--delete-branch");
        if (!string.IsNullOrEmpty(matchHeadCommit))
        {
            args.Add("--match-head-commit");
            args.Add(matchHeadCommit);
        }
        return [.. args];
    }

    private static List<string> BuildPrListArgs(string repoSlug, PrListFilters filters)
    {
        var args = new List<string> { "pr", "list", "--repo", repoSlug };
        if (filters.Head is not null)
        {
            args.AddRange(["--head", filters.Head]);
        }
        if (filters.Base is not null)
        {
            args.AddRange(["--base", filters.Base]);
        }
        if (filters.State is not null)
        {
            args.AddRange(["--state", filters.State]);
        }
        if (filters.Limit is int limit)
        {
            args.AddRange(["--limit", limit.ToString()]);
        }
        args.AddRange(["--json", "number,headRefName,url,mergedAt"]);
        return args;
    }

    private static IReadOnlyList<PullRequestSummary> ParsePrList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            return [];
        }

        var summaries = new List<PullRequestSummary>(array.Count);
        foreach (var item in array)
        {
            if (item is null) continue;
            var number = item["number"]?.GetValue<int>() ?? 0;
            var head = item["headRefName"]?.GetValue<string>() ?? string.Empty;
            var url = item["url"]?.GetValue<string>();
            var mergedAtRaw = item["mergedAt"]?.GetValue<string>();
            DateTimeOffset? mergedAt = null;
            if (!string.IsNullOrEmpty(mergedAtRaw)
                && DateTimeOffset.TryParse(mergedAtRaw, out var parsed))
            {
                mergedAt = parsed;
            }
            summaries.Add(new PullRequestSummary(number, head, url, mergedAt));
        }
        return summaries;
    }

    private static GhPullRequestState? ParsePrState(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch (JsonException) { return null; }
        if (node is not JsonObject obj) return null;

        var number = obj["number"]?.GetValue<int>() ?? 0;
        var state = obj["state"]?.GetValue<string>() ?? string.Empty;
        // mergeCommit is an object {oid: "..."} when present, null otherwise.
        var mergeCommitNode = obj["mergeCommit"];
        string? mergeSha = null;
        if (mergeCommitNode is JsonObject mergeObj)
        {
            mergeSha = mergeObj["oid"]?.GetValue<string>();
        }
        var headRefName = obj["headRefName"]?.GetValue<string>();
        var headRefOid = obj["headRefOid"]?.GetValue<string>();
        return new GhPullRequestState(number, state, mergeSha, headRefName, headRefOid);
    }

    private static GhPullRequestPollData? ParsePrPollData(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch (JsonException) { return null; }
        if (node is not JsonObject obj) return null;

        var number = obj["number"]?.GetValue<int>() ?? 0;
        var state = obj["state"]?.GetValue<string>() ?? string.Empty;
        // gh returns null/empty when no decision exists yet.
        var reviewDecision = obj["reviewDecision"]?.GetValue<string>() ?? string.Empty;
        var mergeable = obj["mergeable"]?.GetValue<string>() ?? "UNKNOWN";
        var headRefName = obj["headRefName"]?.GetValue<string>();
        var headRefOid = obj["headRefOid"]?.GetValue<string>();
        var baseRefName = obj["baseRefName"]?.GetValue<string>();
        var body = obj["body"]?.GetValue<string>() ?? string.Empty;

        var mergeCommitNode = obj["mergeCommit"];
        string? mergeSha = null;
        if (mergeCommitNode is JsonObject mergeObj)
        {
            mergeSha = mergeObj["oid"]?.GetValue<string>();
        }

        DateTimeOffset? mergedAt = null;
        var mergedAtRaw = obj["mergedAt"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(mergedAtRaw)
            && DateTimeOffset.TryParse(mergedAtRaw, out var parsedMergedAt))
        {
            mergedAt = parsedMergedAt;
        }

        var reviews = new List<GhPullRequestReview>();
        if (obj["reviews"] is JsonArray reviewsArray)
        {
            foreach (var item in reviewsArray)
            {
                if (item is not JsonObject reviewObj) continue;
                var login = string.Empty;
                if (reviewObj["author"] is JsonObject authorObj)
                {
                    login = authorObj["login"]?.GetValue<string>() ?? string.Empty;
                }
                var reviewState = reviewObj["state"]?.GetValue<string>() ?? string.Empty;
                DateTimeOffset? submittedAt = null;
                var submittedAtRaw = reviewObj["submittedAt"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(submittedAtRaw)
                    && DateTimeOffset.TryParse(submittedAtRaw, out var parsedAt))
                {
                    submittedAt = parsedAt;
                }
                reviews.Add(new GhPullRequestReview(login, reviewState, submittedAt));
            }
        }

        return new GhPullRequestPollData(
            number,
            state,
            reviewDecision,
            mergeable,
            headRefName,
            headRefOid,
            baseRefName,
            mergeSha,
            mergedAt,
            body,
            reviews);
    }

    /// <summary>
    /// Parse <c>gh pr view --json files</c> output. Returns an empty list
    /// when gh emits a well-formed JSON object with no <c>files</c> key
    /// (very small PRs that only changed metadata can produce that). Returns
    /// null only when the JSON itself is unparseable — caller treats null
    /// as "PR not found".
    /// </summary>
    private static IReadOnlyList<GhPullRequestChangedFile>? ParsePrFiles(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch (JsonException) { return null; }
        if (node is not JsonObject obj) return null;

        var files = new List<GhPullRequestChangedFile>();
        if (obj["files"] is not JsonArray arr) return files;

        foreach (var item in arr)
        {
            if (item is not JsonObject fileObj) continue;
            var path = fileObj["path"]?.GetValue<string>();
            if (string.IsNullOrEmpty(path)) continue;
            var additions = fileObj["additions"]?.GetValue<int>() ?? -1;
            var deletions = fileObj["deletions"]?.GetValue<int>() ?? -1;
            files.Add(new GhPullRequestChangedFile(path, additions, deletions));
        }
        return files;
    }
}
