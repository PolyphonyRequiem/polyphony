using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony plan validate-scope</c> — Phase 3 P8 mechanics for
/// scope renegotiation. Diff-based scope validator, complementary to
/// <see cref="PrCommands.ValidatePlanDiff"/>:
///
/// <list type="bullet">
///   <item><see cref="PrCommands.ValidatePlanDiff"/> classifies a plan
///     PR against the plan-tree taxonomy (parent / ancestor / polyphony
///     state files derived from the work-item id).</item>
///   <item>This verb classifies a plan PR against an arbitrary set of
///     <c>--child-scope</c> globs supplied by the workflow — the four-cell
///     <c>scope × renegotiation flag</c> matrix described in
///     <see cref="PlanValidateScopeResult"/>.</item>
/// </list>
///
/// <para>Always exits 0. Workflow consumers route on
/// <see cref="PlanValidateScopeResult.Verdict"/> +
/// <see cref="PlanValidateScopeResult.ErrorCode"/>.</para>
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>
    /// Classify a plan PR's changed-file set against a set of in-scope
    /// path globs and the renegotiation flag in the PR body.
    /// </summary>
    /// <param name="prNumber">PR number to inspect.</param>
    /// <param name="childScope">
    /// Comma-separated list of globs the child planner was authorized to
    /// touch. Globs use posix-glob-ish semantics (see
    /// <see cref="ScopeGlobMatcher"/>): <c>*</c> matches within a single
    /// segment, <c>**</c> crosses segments, <c>?</c> matches one
    /// non-separator char, everything else literal. Whitespace around
    /// each glob is trimmed; empty entries are dropped.
    /// </param>
    /// <param name="repo">Optional <c>owner/repo</c> override; defaults to the slug derived from the current checkout's <c>origin</c> remote.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("validate-scope")]
    [VerbResult(typeof(PlanValidateScopeResult))]
    public async Task<int> ValidateScope(
        int prNumber = RequiredInput.MissingInt,
        string childScope = "",
        string repo = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("plan validate-scope",
            ("--pr-number", prNumber == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (prNumber <= 0)
        {
            EmitScopeError(prNumber,
                "config_error",
                $"prNumber must be positive (got {prNumber})");
            return ExitCodes.Success;
        }

        var globs = ParseChildScope(childScope);

        // ── 1. Resolve repo slug. ─────────────────────────────────────────
        var slug = string.IsNullOrWhiteSpace(repo)
            ? await TryResolveSlugAsync(ct).ConfigureAwait(false)
            : repo.Trim();
        if (string.IsNullOrEmpty(slug))
        {
            EmitScopeError(prNumber,
                "repo_not_resolved",
                "Could not resolve repo slug from origin remote (pass --repo to override).");
            return ExitCodes.Success;
        }

        // ── 2. Poll PR body (for the flag). ───────────────────────────────
        GhPullRequestPollData? poll;
        try
        {
            poll = await gh.GetPullRequestPollDataAsync(slug, prNumber, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExternalToolTimeoutException ex)
        {
            EmitScopeError(prNumber, "gh_timeout",
                ex.FormatErrorMessage("gh pr view"));
            return ExitCodes.Success;
        }
        catch (ExternalToolException ex)
        {
            EmitScopeError(prNumber, "gh_failed", $"gh pr view failed: {ex.Message}");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitScopeError(prNumber, "gh_failed", $"gh pr view failed: {ex.Message}");
            return ExitCodes.Success;
        }

        if (poll is null)
        {
            EmitScopeError(prNumber, "pr_not_found", $"PR #{prNumber} not found in {slug}.");
            return ExitCodes.Success;
        }

        // ── 3. Fetch the changed-file list. We use the same JSON-files
        //      endpoint as validate-plan-diff so the verb does not depend
        //      on `gh pr diff` parse-ability — and so a giant binary diff
        //      doesn't pull MB of bytes through the runner.
        IReadOnlyList<GhPullRequestChangedFile>? files;
        try
        {
            files = await gh.GetPullRequestFilesAsync(slug, prNumber, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExternalToolTimeoutException ex)
        {
            EmitScopeError(prNumber, "gh_timeout",
                ex.FormatErrorMessage("gh pr view --json files"));
            return ExitCodes.Success;
        }
        catch (ExternalToolException ex)
        {
            EmitScopeError(prNumber, "gh_failed", $"gh pr view --json files failed: {ex.Message}");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitScopeError(prNumber, "gh_failed", $"gh pr view --json files failed: {ex.Message}");
            return ExitCodes.Success;
        }

        if (files is null)
        {
            EmitScopeError(prNumber, "pr_not_found",
                $"PR #{prNumber} files endpoint returned no payload in {slug}.");
            return ExitCodes.Success;
        }

        // ── 4. Extract the renegotiation flag. ────────────────────────────
        var extracted = RenegotiationFlagExtractor.Extract(poll.Body);
        // Malformed counts as "flag absent" for the matrix — the
        // extract-renegotiation-flag verb is the right place to surface
        // the malformed-block warning to a human; here we are deciding
        // whether the diff is allowed.
        var flagPresent = extracted.FlagPresent;

        // ── 5. Classify each path. ────────────────────────────────────────
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allTouched = new List<string>(files.Count);
        var inScope = new List<string>();
        var outOfScope = new List<string>();
        foreach (var f in files)
        {
            var p = f.Path;
            if (string.IsNullOrEmpty(p)) continue;
            if (!seen.Add(p)) continue;
            allTouched.Add(p);
            if (ScopeGlobMatcher.IsMatchAny(p, globs))
                inScope.Add(p);
            else
                outOfScope.Add(p);
        }

        var parentTouched = outOfScope.Count > 0;
        string verdict;
        string? errorCode = null;
        string? errorMessage = null;
        var warnings = new List<string>();

        if (parentTouched && !flagPresent)
        {
            verdict = "block";
            errorCode = "scope_violation_no_flag";
            errorMessage =
                $"PR #{prNumber} touches {outOfScope.Count} file(s) outside the supplied --child-scope " +
                "and does not declare a renegotiation flag " +
                "(<!-- polyphony:requests-parent-change --> ... <!-- /polyphony:requests-parent-change -->).";
        }
        else if (flagPresent && !parentTouched)
        {
            verdict = "allow";
            warnings.Add("flag_without_parent_files");
        }
        else
        {
            // (flag && parentTouched) OR (!flag && !parentTouched)
            verdict = "allow";
        }

        EmitScope(new PlanValidateScopeResult
        {
            Success = true,
            PrNumber = prNumber,
            FilesTouched = allTouched,
            FilesInScope = inScope,
            FilesOutOfScope = outOfScope,
            FlagPresent = flagPresent,
            Verdict = verdict,
            Warnings = warnings,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Decompose the <c>--child-scope</c> CLI flag into a glob list. The
    /// flag is comma-separated to mirror <c>ParseAncestorIds</c>'s
    /// convention; a workflow that needs commas inside a glob (none of
    /// the Phase 3 plan-tree paths do) can pass the glob via a heredoc /
    /// repeated invocations of the verb.
    /// </summary>
    internal static IReadOnlyList<string> ParseChildScope(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length == 0) continue;
            if (!seen.Add(token)) continue;
            result.Add(token);
        }
        return result;
    }

    private static void EmitScope(PlanValidateScopeResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PlanValidateScopeResult));

    private static void EmitScopeError(int prNumber, string code, string message)
        => EmitScope(new PlanValidateScopeResult
        {
            Success = false,
            PrNumber = prNumber,
            FilesTouched = Array.Empty<string>(),
            FilesInScope = Array.Empty<string>(),
            FilesOutOfScope = Array.Empty<string>(),
            FlagPresent = false,
            Verdict = "block",
            Warnings = Array.Empty<string>(),
            ErrorCode = code,
            ErrorMessage = message,
        });
}
