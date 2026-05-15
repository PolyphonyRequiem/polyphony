using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc.Observers;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony plan extract-parent-patch</c> — produce a deterministic
/// markdown patch for a single parent's plan file out of an approved
/// child plan PR that carries <c>requests_parent_change: true</c>.
///
/// <para>Per P8d design (Q6): the parent architect should NOT be asked
/// to infer the requested change from an arbitrary PR. This verb extracts
/// just the hunks that touch <c>plans/plan-{parent_item_id}.md</c>,
/// bounds the size, and emits a single deterministic JSON record so the
/// parent's replan loop has a reproducible artifact to consume.</para>
///
/// <para>Always exits 0 (routing-style verb). Workflow branches on the
/// presence of <see cref="PlanExtractParentPatchResult.Error"/>.</para>
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>Default upper bound for the rendered diff (50 KB).</summary>
    private const int DefaultDiffSizeLimitBytes = 50 * 1024;

    /// <summary>Truncation notice appended when the rendered diff overflows the cap.</summary>
    private const string TruncationNotice =
        "\n... [truncated by polyphony plan extract-parent-patch — see PR for full diff] ...\n";

    /// <summary>
    /// Extract the parent-plan hunks from a child plan PR.
    /// </summary>
    /// <param name="prUrl">Full PR URL — accepts both
    /// <c>https://github.com/owner/repo/pull/N</c> and
    /// <c>https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/N</c>.
    /// The URL is parsed to derive the platform identity + PR number.</param>
    /// <param name="rootId">Root work item ID (defines the snapshot key namespace).</param>
    /// <param name="parentItemId">Parent work item ID whose plan file we want hunks for.</param>
    /// <param name="diffSizeLimitBytes">Upper bound for the rendered parent-scoped diff. Defaults to 50 KB.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("extract-parent-patch")]
    [VerbResult(typeof(PlanExtractParentPatchResult))]
    public async Task<int> ExtractParentPatch(
        string prUrl = "",
        int rootId = RequiredInput.MissingInt,
        int parentItemId = RequiredInput.MissingInt,
        int diffSizeLimitBytes = DefaultDiffSizeLimitBytes,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("plan extract-parent-patch",
            ("--pr-url", string.IsNullOrEmpty(prUrl)),
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--parent-item-id", parentItemId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        // ── 1. Validate inputs. ───────────────────────────────────────────
        if (!RootId.TryParse(rootId, out _))
        {
            EmitError(prUrl, parentItemId, "rootId must be positive", "invalid_root_id");
            return ExitCodes.Success;
        }
        if (!WorkItemId.TryParse(parentItemId, out _))
        {
            EmitError(prUrl, parentItemId, "parentItemId must be positive", "invalid_parent_item_id");
            return ExitCodes.Success;
        }
        if (diffSizeLimitBytes <= 0)
        {
            EmitError(prUrl, parentItemId, "diffSizeLimitBytes must be positive", "invalid_size_limit");
            return ExitCodes.Success;
        }

        // ── 2. Parse the PR URL into platform identity + PR number. ───────
        // Accepts both github.com and dev.azure.com / *.visualstudio.com PR
        // URLs (Phase 6 — ADO parity for cascade-remedy.yaml +
        // remedy-stale-descendant.yaml).
        if (!TryParsePrUrl(prUrl, out var identity, out var resolvedPrNumber))
        {
            EmitError(prUrl, parentItemId,
                $"could not parse pr url '{prUrl}' (expected https://github.com/owner/repo/pull/N or https://dev.azure.com/{{org}}/{{project}}/_git/{{repo}}/pullrequest/N)",
                "invalid_pr_url");
            return ExitCodes.Success;
        }

        var slug = PullRequestReader.BuildRepoSlug(identity!);
        var canonicalPrUrl = prUrl;

        // ── 3. Fetch poll data — gives us body (front-matter) + headSha + headRef + baseRef. ─
        GhPullRequestPollData? pollData;
        try
        {
            pollData = await pullRequestReader.GetPollDataAsync(identity!, resolvedPrNumber, ct).ConfigureAwait(false);
        }
        catch (ExternalToolTimeoutException ex)
        {
            EmitError(canonicalPrUrl, parentItemId,
                ex.FormatErrorMessage("gh pr view"),
                "gh_timeout", slug, resolvedPrNumber);
            return ExitCodes.Success;
        }
        catch (ExternalToolException ex)
        {
            EmitError(canonicalPrUrl, parentItemId, $"gh pr view failed: {ex.Message}",
                "gh_failed", slug, resolvedPrNumber);
            return ExitCodes.Success;
        }

        if (pollData is null)
        {
            EmitError(canonicalPrUrl, parentItemId, $"PR #{resolvedPrNumber} not found in {slug}",
                "pr_not_found", slug, resolvedPrNumber);
            return ExitCodes.Success;
        }

        // ── 4. Front-matter (lenient — warn rather than fail). ────────────
        var frontMatter = PlanPrFrontMatter.Parse(pollData.Body ?? string.Empty);
        var warnings = new List<string>();

        if (!frontMatter.RequestsParentChange)
        {
            warnings.Add(
                "PR body's `requests_parent_change` flag is not set to true. " +
                "The diff is still extracted for inspection; the parent replan loop should refuse to act on it.");
        }

        // Determine snapshot key for the parent's plan generation.
        // Convention: "root" when parent IS root; "<parent_item_id>" otherwise.
        var snapshotKey = parentItemId == rootId
            ? "root"
            : parentItemId.ToString(CultureInfo.InvariantCulture);

        int? expectedParentGeneration = null;
        if (frontMatter.AncestorPlanGenerations.TryGetValue(snapshotKey, out var gen))
        {
            expectedParentGeneration = gen;
        }
        else
        {
            warnings.Add(
                $"PR's ancestor_plan_generations snapshot has no entry for parent key '{snapshotKey}'. " +
                "Cannot validate against the current parent generation.");
        }

        // ── 5. Infer child item id from the PR's head ref. ────────────────
        int? childItemId = null;
        var headRef = pollData.HeadRefName ?? string.Empty;
        var parsedBranch = BranchNameParser.ParseOrUnrecognized(headRef);
        switch (parsedBranch)
        {
            case ParsedBranch.RootPlan rp:
                childItemId = rp.RootId.Value;
                break;
            case ParsedBranch.DescendantPlan dp:
                childItemId = dp.ItemId.Value;
                break;
            default:
                warnings.Add(
                    $"PR head ref '{headRef}' is not a recognized plan branch — child_item_id unknown.");
                break;
        }

        // ── 6. Compute unified diff. ──────────────────────────────────────
        // GitHub: `gh pr diff` (CLI handles the 3-dot semantics + auth).
        // ADO:    local-git 3-dot diff (REST diff composer is too fragile
        //         for rename/delete/binary edge cases — BLOCKER #4 / Phase 6).
        string rawDiff;
        if (identity is RepoIdentity.GitHubRepo ghIdent)
        {
            string? rawDiffResult;
            try
            {
                rawDiffResult = await gh.GetPullRequestDiffAsync(
                    PullRequestReader.BuildRepoSlug(ghIdent),
                    resolvedPrNumber,
                    ct).ConfigureAwait(false);
            }
            catch (ExternalToolTimeoutException ex)
            {
                EmitError(canonicalPrUrl, parentItemId,
                    ex.FormatErrorMessage("gh pr diff"),
                    "gh_timeout", slug, resolvedPrNumber);
                return ExitCodes.Success;
            }
            catch (ExternalToolException ex)
            {
                EmitError(canonicalPrUrl, parentItemId, $"gh pr diff failed: {ex.Message}",
                    "gh_failed", slug, resolvedPrNumber);
                return ExitCodes.Success;
            }

            if (string.IsNullOrEmpty(rawDiffResult))
            {
                EmitError(canonicalPrUrl, parentItemId,
                    $"gh pr diff returned no output for PR #{resolvedPrNumber}",
                    "diff_unavailable", slug, resolvedPrNumber);
                return ExitCodes.Success;
            }
            rawDiff = rawDiffResult;
        }
        else
        {
            // ADO leg: local-git 3-dot diff (base...head).
            var baseRef = pollData.BaseRefName ?? string.Empty;
            if (string.IsNullOrEmpty(headRef) || string.IsNullOrEmpty(baseRef))
            {
                EmitError(canonicalPrUrl, parentItemId,
                    $"PR #{resolvedPrNumber} returned empty head or base ref; cannot compute local diff.",
                    "diff_unavailable", slug, resolvedPrNumber);
                return ExitCodes.Success;
            }

            try
            {
                await git.FetchAsync("origin", baseRef, ct).ConfigureAwait(false);
                await git.FetchAsync("origin", headRef, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                EmitError(canonicalPrUrl, parentItemId,
                    $"git fetch failed for {baseRef}/{headRef}: {ex.Message}",
                    "git_failed", slug, resolvedPrNumber);
                return ExitCodes.Success;
            }

            try
            {
                rawDiff = await git.DiffAsync(
                    $"origin/{baseRef}",
                    $"origin/{headRef}",
                    threeDot: true,
                    ct).ConfigureAwait(false);
            }
            catch (ExternalToolTimeoutException ex)
            {
                EmitError(canonicalPrUrl, parentItemId,
                    ex.FormatErrorMessage("git diff"),
                    "git_timeout", slug, resolvedPrNumber);
                return ExitCodes.Success;
            }
            catch (ExternalToolException ex)
            {
                EmitError(canonicalPrUrl, parentItemId, $"git diff failed: {ex.Message}",
                    "git_failed", slug, resolvedPrNumber);
                return ExitCodes.Success;
            }
        }

        // ── 7. Filter diff to parent plan file hunks. ─────────────────────
        var parentPlanPath = $"plans/plan-{parentItemId}.md";
        var (filteredDiff, filesTouched) = ExtractFileFromUnifiedDiff(rawDiff, parentPlanPath);

        if (filesTouched.Count == 0)
        {
            warnings.Add(
                $"PR diff does not touch the parent plan file '{parentPlanPath}'. " +
                "Nothing to extract.");
        }

        // ── 8. Bound size. ────────────────────────────────────────────────
        var diffBytes = Encoding.UTF8.GetByteCount(filteredDiff);
        var truncated = false;
        var bounded = filteredDiff;
        if (diffBytes > diffSizeLimitBytes)
        {
            bounded = TruncateUtf8(filteredDiff, diffSizeLimitBytes - Encoding.UTF8.GetByteCount(TruncationNotice))
                + TruncationNotice;
            truncated = true;
        }

        // ── 9. Emit. ──────────────────────────────────────────────────────
        var result = new PlanExtractParentPatchResult
        {
            PrUrl = canonicalPrUrl,
            PrNumber = resolvedPrNumber,
            RepoSlug = slug,
            ChildItemId = childItemId,
            ParentItemId = parentItemId,
            ExpectedParentGeneration = expectedParentGeneration,
            HeadSha = pollData.HeadRefOid,
            FilesTouched = filesTouched,
            ParentPlanDiff = bounded,
            Truncated = truncated,
            DiffSizeBytes = diffBytes,
            RequestsParentChange = frontMatter.RequestsParentChange,
            Warnings = warnings,
        };
        EmitExtractParentPatch(result);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Extract the file-scoped slice of a unified diff. Returns the
    /// substring spanning every <c>diff --git</c> block whose target path
    /// matches <paramref name="targetPath"/> (compared case-sensitively
    /// against both old and new path forms — handles renames defensively),
    /// plus the list of paths actually matched.
    /// </summary>
    /// <remarks>
    /// We parse the unified diff structurally rather than line-by-line
    /// regex matching so that arbitrary diff content (which can contain
    /// strings that look like <c>diff --git</c> inside hunk bodies) does
    /// not confuse the boundary detection. The header line is the only
    /// boundary that matters, and `gh pr diff` always emits one per file.
    /// </remarks>
    internal static (string Diff, IReadOnlyList<string> FilesTouched) ExtractFileFromUnifiedDiff(
        string rawDiff,
        string targetPath)
    {
        if (string.IsNullOrEmpty(rawDiff))
        {
            return (string.Empty, []);
        }

        var sb = new StringBuilder();
        var matched = new List<string>();
        var lines = rawDiff.Split('\n');

        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (!line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // Collect the block: from this header up to (but not including)
            // the next "diff --git" header (or end of input).
            var blockStart = i;
            int blockEnd = lines.Length;
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    blockEnd = j;
                    break;
                }
            }

            // Determine the target path. The header looks like:
            //   diff --git a/plans/plan-456.md b/plans/plan-456.md
            // For renames, the a/ and b/ paths differ. Match either side.
            var pathsInHeader = ParseDiffGitHeaderPaths(line);
            bool headerMatches = pathsInHeader.Any(p => string.Equals(p, targetPath, StringComparison.Ordinal));

            // Renames: also check explicit "rename to" / "rename from" lines
            // inside the block (some emitters use empty a/b in the header).
            if (!headerMatches)
            {
                for (var k = blockStart + 1; k < blockEnd; k++)
                {
                    var inner = lines[k];
                    if ((inner.StartsWith("rename from ", StringComparison.Ordinal)
                            && string.Equals(inner["rename from ".Length..], targetPath, StringComparison.Ordinal))
                        || (inner.StartsWith("rename to ", StringComparison.Ordinal)
                            && string.Equals(inner["rename to ".Length..], targetPath, StringComparison.Ordinal))
                        || (inner.StartsWith("--- a/", StringComparison.Ordinal)
                            && string.Equals(inner["--- a/".Length..], targetPath, StringComparison.Ordinal))
                        || (inner.StartsWith("+++ b/", StringComparison.Ordinal)
                            && string.Equals(inner["+++ b/".Length..], targetPath, StringComparison.Ordinal)))
                    {
                        headerMatches = true;
                        break;
                    }
                }
            }

            if (headerMatches)
            {
                if (!matched.Contains(targetPath, StringComparer.Ordinal))
                {
                    matched.Add(targetPath);
                }
                for (var k = blockStart; k < blockEnd; k++)
                {
                    sb.Append(lines[k]);
                    if (k < blockEnd - 1 || blockEnd < lines.Length)
                    {
                        sb.Append('\n');
                    }
                }
            }

            i = blockEnd;
        }

        return (sb.ToString(), matched);
    }

    private static IEnumerable<string> ParseDiffGitHeaderPaths(string headerLine)
    {
        // Format: "diff --git a/<old> b/<new>"
        // Paths may not contain whitespace in this convention; gh pr diff
        // does not produce quoted paths in the header line.
        const string prefix = "diff --git ";
        if (!headerLine.StartsWith(prefix, StringComparison.Ordinal))
        {
            yield break;
        }
        var rest = headerLine[prefix.Length..];
        var lastSpace = rest.LastIndexOf(' ');
        if (lastSpace <= 0) yield break;
        var aPath = rest[..lastSpace];
        var bPath = rest[(lastSpace + 1)..];
        if (aPath.StartsWith("a/", StringComparison.Ordinal)) aPath = aPath[2..];
        if (bPath.StartsWith("b/", StringComparison.Ordinal)) bPath = bPath[2..];
        yield return aPath;
        yield return bPath;
    }

    /// <summary>
    /// Truncate a string to the largest prefix that fits within
    /// <paramref name="maxBytes"/> when UTF-8 encoded, never splitting
    /// a multi-byte codepoint.
    /// </summary>
    internal static string TruncateUtf8(string text, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(text)) return string.Empty;
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= maxBytes) return text;

        var enc = Encoding.UTF8;
        var buffer = enc.GetBytes(text);
        var safeLen = Math.Min(maxBytes, buffer.Length);
        // Walk back to the last full codepoint boundary.
        while (safeLen > 0 && (buffer[safeLen - 1] & 0xC0) == 0x80)
        {
            safeLen--;
        }
        return enc.GetString(buffer, 0, safeLen);
    }

    private static bool TryParsePrUrl(string prUrl, out RepoIdentity? identity, out int prNumber)
    {
        identity = null;
        prNumber = 0;
        if (string.IsNullOrWhiteSpace(prUrl)) return false;

        // GitHub: https://github.com/owner/repo/pull/N
        var ghMatch = System.Text.RegularExpressions.Regex.Match(
            prUrl,
            @"^https?://github\.com/([^/]+)/([^/]+)/pull/(\d+)(?:[/?#].*)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (ghMatch.Success && int.TryParse(ghMatch.Groups[3].Value, out prNumber))
        {
            identity = new RepoIdentity.GitHubRepo(ghMatch.Groups[1].Value, ghMatch.Groups[2].Value);
            return true;
        }

        // ADO modern: https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/N
        var adoMatch = System.Text.RegularExpressions.Regex.Match(
            prUrl,
            @"^https?://dev\.azure\.com/([^/]+)/([^/]+)/_git/([^/]+)/pullrequest/(\d+)(?:[/?#].*)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (adoMatch.Success && int.TryParse(adoMatch.Groups[4].Value, out prNumber))
        {
            identity = new RepoIdentity.AdoRepo(
                adoMatch.Groups[1].Value, adoMatch.Groups[2].Value, adoMatch.Groups[3].Value);
            return true;
        }

        // ADO legacy: https://{org}.visualstudio.com/{project}/_git/{repo}/pullrequest/N
        var adoLegacyMatch = System.Text.RegularExpressions.Regex.Match(
            prUrl,
            @"^https?://([^.]+)\.visualstudio\.com/([^/]+)/_git/([^/]+)/pullrequest/(\d+)(?:[/?#].*)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (adoLegacyMatch.Success && int.TryParse(adoLegacyMatch.Groups[4].Value, out prNumber))
        {
            identity = new RepoIdentity.AdoRepo(
                adoLegacyMatch.Groups[1].Value, adoLegacyMatch.Groups[2].Value, adoLegacyMatch.Groups[3].Value);
            return true;
        }

        prNumber = 0;
        return false;
    }

    private static void EmitError(
        string prUrl,
        int parentItemId,
        string error,
        string code,
        string slug = "",
        int prNumber = 0)
        => EmitExtractParentPatch(new PlanExtractParentPatchResult
        {
            PrUrl = prUrl,
            PrNumber = prNumber,
            RepoSlug = slug,
            ParentItemId = parentItemId,
            Error = error,
            ErrorCode = code,
        });

    private static void EmitExtractParentPatch(PlanExtractParentPatchResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PlanExtractParentPatchResult));
}
