using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony plan extract-renegotiation-flag</c> — Phase 3 P8
/// mechanics for scope renegotiation.
///
/// <para>A child planner that discovers its scope was wrong (e.g. the
/// parent assumed feature/X was already in main when it is still in
/// flight) embeds a <c>requests-parent-change</c> HTML-comment-fenced
/// block in the plan PR's body. This verb fetches the PR body and
/// extracts the flag + reason for the downstream re-entry workflow
/// (<c>p3-renegotiation-handler</c>) to consume.</para>
///
/// <para>Always exits 0. Workflow consumers route on
/// <see cref="PlanExtractRenegotiationFlagResult.Success"/> +
/// <see cref="PlanExtractRenegotiationFlagResult.ErrorCode"/>.</para>
///
/// <para>Mirrors PR #133's <c>polyphony guidance extract</c> fenced-block
/// idiom (HTML comments, Singleline regex, multi-block concatenation) —
/// see <see cref="RenegotiationFlagExtractor"/> for the convention.</para>
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>
    /// Extract the renegotiation flag and reason from a plan PR's body.
    /// </summary>
    /// <param name="prNumber">PR number to inspect.</param>
    /// <param name="repo">Optional <c>owner/repo</c> override; defaults to the slug derived from the current checkout's <c>origin</c> remote.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("extract-renegotiation-flag")]
    [VerbResult(typeof(PlanExtractRenegotiationFlagResult))]
    public async Task<int> ExtractRenegotiationFlag(
        int prNumber = RequiredInput.MissingInt,
        string repo = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("plan extract-renegotiation-flag",
            ("--pr-number", prNumber == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (prNumber <= 0)
        {
            EmitRenegotiationError(prNumber,
                "config_error",
                $"prNumber must be positive (got {prNumber})");
            return ExitCodes.Success;
        }

        // ── 1. Resolve repo slug (explicit override wins). ────────────────
        var slug = string.IsNullOrWhiteSpace(repo)
            ? await TryResolveSlugAsync(ct).ConfigureAwait(false)
            : repo.Trim();
        if (string.IsNullOrEmpty(slug))
        {
            EmitRenegotiationError(prNumber,
                "repo_not_resolved",
                "Could not resolve repo slug from origin remote (pass --repo to override).");
            return ExitCodes.Success;
        }

        // ── 2. Fetch PR body. ─────────────────────────────────────────────
        GhPullRequestPollData? poll;
        try
        {
            poll = await gh.GetPullRequestPollDataAsync(slug, prNumber, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExternalToolTimeoutException ex)
        {
            EmitRenegotiationError(prNumber,
                "gh_timeout",
                $"gh pr view timed out after {ex.Attempts} attempt(s).");
            return ExitCodes.Success;
        }
        catch (ExternalToolException ex)
        {
            EmitRenegotiationError(prNumber, "gh_failed", $"gh pr view failed: {ex.Message}");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            EmitRenegotiationError(prNumber, "gh_failed", $"gh pr view failed: {ex.Message}");
            return ExitCodes.Success;
        }

        if (poll is null)
        {
            EmitRenegotiationError(prNumber, "pr_not_found", $"PR #{prNumber} not found in {slug}.");
            return ExitCodes.Success;
        }

        // ── 3. Extract. ───────────────────────────────────────────────────
        var extracted = RenegotiationFlagExtractor.Extract(poll.Body);

        if (extracted.Status == RenegotiationFlagExtractor.ExtractStatus.Malformed)
        {
            EmitRenegotiation(new PlanExtractRenegotiationFlagResult
            {
                Success = true,
                PrNumber = prNumber,
                FlagPresent = false,
                RenegotiationRequest = null,
                FencedBlockWellFormed = false,
                ErrorCode = "malformed_renegotiation_block",
                ErrorMessage =
                    $"PR #{prNumber} body has an opening '<!-- polyphony:requests-parent-change -->' tag with no matching closing tag.",
            });
            return ExitCodes.Success;
        }

        EmitRenegotiation(new PlanExtractRenegotiationFlagResult
        {
            Success = true,
            PrNumber = prNumber,
            FlagPresent = extracted.FlagPresent,
            RenegotiationRequest = extracted.Reason,
            FencedBlockWellFormed = true,
            ErrorCode = null,
            ErrorMessage = null,
        });
        return ExitCodes.Success;
    }

    private static void EmitRenegotiation(PlanExtractRenegotiationFlagResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PlanExtractRenegotiationFlagResult));

    private static void EmitRenegotiationError(int prNumber, string code, string message)
        => EmitRenegotiation(new PlanExtractRenegotiationFlagResult
        {
            Success = false,
            PrNumber = prNumber,
            FlagPresent = false,
            RenegotiationRequest = null,
            FencedBlockWellFormed = true,
            ErrorCode = code,
            ErrorMessage = message,
        });
}
