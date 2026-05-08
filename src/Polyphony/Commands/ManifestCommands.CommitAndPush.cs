using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony manifest commit-and-push</c> — the manifest-lifecycle
/// owner verb that satisfies the branch-model ADR's "every run commits
/// <c>.polyphony/run.yaml</c> on the feature branch" invariant.
///
/// <para>Replaces ad-hoc YAML/PowerShell that wrote the manifest locally
/// but never committed it. The verb is idempotent and routing-style so
/// the workflow can call it on every preflight without checking
/// precursor state.</para>
/// </summary>
public sealed partial class ManifestCommands
{
    /// <summary>
    /// Validate the manifest at <paramref name="path"/> matches
    /// <paramref name="rootId"/>, ensure the worktree is on
    /// <c>feature/{rootId}</c>, then stage, commit, and push the
    /// manifest to <c>origin/feature/{rootId}</c>. No-op when the
    /// manifest at HEAD already matches the on-disk file.
    /// </summary>
    /// <param name="rootId">Apex (run-root) work-item id. Must equal the manifest's <c>root_id</c>.</param>
    /// <param name="message">Commit message. Defaults to <c>manifest: bootstrap (root {rootId})</c>.</param>
    /// <param name="path">Path to the manifest. Defaults to <c>.polyphony/run.yaml</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("commit-and-push")]
    [VerbResult(typeof(ManifestCommitAndPushResult))]
    public async Task<int> CommitAndPush(
        int rootId,
        string message = "",
        string path = RunManifestStore.DefaultRelativePath,
        CancellationToken ct = default)
    {
        // 1. Input validation. Routing-style: emit an envelope and exit 0
        //    so the workflow can route on `error_code`, not on exit code.
        if (!RootId.TryParse(rootId, out var root))
        {
            EmitError(rootId, path, branch: string.Empty,
                errorCode: "invalid_inputs",
                error: $"--root-id must be positive (got {rootId}).");
            return ExitCodes.Success;
        }

        var expectedBranch = BranchNameBuilder.Feature(root).Value;
        var commitMessage = string.IsNullOrWhiteSpace(message)
            ? $"manifest: bootstrap (root {rootId})"
            : message;

        // 2. Manifest must exist on disk. The bootstrap step is expected
        //    to have written it; if it isn't here we surface a routing
        //    error rather than try to synthesize one.
        if (!File.Exists(path))
        {
            EmitError(rootId, path, expectedBranch,
                errorCode: "manifest_missing",
                error: $"manifest not found at '{path}'. Run 'polyphony manifest init' (or the workflow's init_manifest step) before commit-and-push.");
            return ExitCodes.Success;
        }

        // 3. Manifest must parse and its root_id must match the requested
        //    root. This is the contract the downstream pr open-plan-pr
        //    reads and enforces; we catch the same drift here.
        RunManifest manifest;
        try
        {
            manifest = RunManifestStore.LoadOrThrow(path);
        }
        catch (Exception ex)
        {
            EmitError(rootId, path, expectedBranch,
                errorCode: "manifest_parse_failed",
                error: $"could not parse manifest at '{path}': {ex.Message}");
            return ExitCodes.Success;
        }

        if (manifest.RootId != rootId)
        {
            EmitError(rootId, path, expectedBranch,
                errorCode: "manifest_root_mismatch",
                error: $"manifest root_id={manifest.RootId} does not match --root-id={rootId}. The worktree is locked to a different apex run.");
            return ExitCodes.Success;
        }

        try
        {
            // 4. Worktree must be on feature/{rootId}. The branch-model
            //    ADR is explicit that the manifest lives on the feature
            //    branch; committing it from any other branch would
            //    corrupt the contract. We refuse rather than checking
            //    out — the caller is the apex driver, and a wrong branch
            //    here is a workflow-authoring bug, not a recoverable
            //    drift. The verb surfaces the violation; the workflow
            //    routes to its preflight failure gate.
            var currentBranch = await git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
            if (!string.Equals(currentBranch, expectedBranch, StringComparison.Ordinal))
            {
                EmitError(rootId, path, expectedBranch,
                    errorCode: "wrong_branch",
                    error: $"worktree is on '{currentBranch ?? "(detached)"}', expected '{expectedBranch}'. The manifest must be committed on the feature branch.");
                return ExitCodes.Success;
            }

            // 5. Stage the manifest. `git add` succeeds whether or not
            //    the file was new/modified — staged-or-not determined by
            //    the porcelain status check that follows.
            await git.StageAsync(path, ct).ConfigureAwait(false);

            // 6. Idempotency check: porcelain row format is "XY filename"
            //    where X is the index status. Non-space, non-? in column
            //    X means staged-for-commit. Anything else (clean, only
            //    unstaged changes) means there's nothing for us to do.
            var status = await git.GetStatusAsync(ct).ConfigureAwait(false);
            var manifestStaged = status.Any(s =>
                s.Length >= 2 &&
                s[0] != ' ' &&
                s[0] != '?' &&
                s.Substring(2).Trim().Replace('\\', '/')
                    .Equals(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

            if (!manifestStaged)
            {
                EmitNoOp(rootId, path, expectedBranch);
                return ExitCodes.Success;
            }

            // 7. Commit + push. Errors propagate as ExternalToolException
            //    and are caught below and surfaced as `git_failed`.
            await git.CommitAsync(commitMessage, ct).ConfigureAwait(false);
            await git.PushAsync(expectedBranch, ct: ct).ConfigureAwait(false);

            var sha = await git.RevParseLocalBranchAsync(expectedBranch, ct).ConfigureAwait(false);

            EmitSuccess(rootId, path, expectedBranch, sha);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (ExternalToolException ex)
        {
            EmitError(rootId, path, expectedBranch,
                errorCode: "git_failed",
                error: ex.Message);
            return ExitCodes.Success;
        }
    }

    private static void EmitSuccess(int rootId, string path, string branch, string? sha)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new ManifestCommitAndPushResult
            {
                Branch = branch,
                Path = path,
                Pushed = true,
                RootId = rootId,
                CommitSha = sha,
            },
            PolyphonyJsonContext.Default.ManifestCommitAndPushResult));
    }

    private static void EmitNoOp(int rootId, string path, string branch)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new ManifestCommitAndPushResult
            {
                Branch = branch,
                Path = path,
                Pushed = false,
                RootId = rootId,
                NoOpReason = "no_changes",
            },
            PolyphonyJsonContext.Default.ManifestCommitAndPushResult));
    }

    private static void EmitError(int rootId, string path, string branch, string errorCode, string error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new ManifestCommitAndPushResult
            {
                Branch = branch,
                Path = path,
                Pushed = false,
                RootId = rootId,
                ErrorCode = errorCode,
                Error = error,
            },
            PolyphonyJsonContext.Default.ManifestCommitAndPushResult));
    }
}
