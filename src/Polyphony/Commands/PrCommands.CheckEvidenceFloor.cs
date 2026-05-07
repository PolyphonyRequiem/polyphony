using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Phase 6 PR #7 — mechanical pre-reviewer floor for actionable
    /// evidence PRs. Verifies the PR has at least <paramref name="minCommits"/>
    /// (default 1) commits on its head branch beyond base AND a non-empty
    /// PR body (after whitespace trim). The floor exists to catch
    /// misfires (the actionable_agent crashed before producing anything)
    /// before the LLM evidence_reviewer is asked to judge an empty PR.
    /// Beyond the floor, the reviewer is the only gate on whether
    /// content is "good enough" — do not extend this verb with
    /// content-quality checks (per Phase 6 design sketch pick #6).
    /// </summary>
    /// <remarks>
    /// <para><b>Routing-style envelope.</b> The verb always exits 0.
    /// Floor outcomes are conveyed via <c>passes_floor</c> +
    /// <c>violations</c>; transport failures (PR not found, gh process
    /// failure) are conveyed via <c>error_code</c> /
    /// <c>error_message</c>. The two are mutually exclusive: when the
    /// verb cannot read the PR at all, <c>success=false</c> and
    /// <c>passes_floor=false</c>.</para>
    ///
    /// <para><b>Repo slug resolution.</b> When <paramref name="repo"/> is
    /// supplied it is passed verbatim to <c>gh --repo</c>. When omitted,
    /// the slug is derived from <c>git remote get-url origin</c> via the
    /// shared <c>TryResolveSlugAsync</c> helper — same pattern as the
    /// sibling <c>open-evidence-pr</c> / <c>create-feature-pr</c>
    /// verbs.</para>
    ///
    /// <para><b>Violations.</b> Stable machine-readable rule names,
    /// listed in declaration order: <c>no_commits</c> when
    /// <c>commit_count &lt; minCommits</c>, <c>empty_body</c> when the
    /// trimmed body length is zero. Both can fire on the same PR.</para>
    ///
    /// <para><b>GH-only.</b> Per the Phase 6 design sketch, the
    /// evidence-PR concept is GH-only for now. An ADO sibling
    /// (<c>pr check-evidence-floor-ado</c>) ships with ADO actionable
    /// support if/when needed.</para>
    /// </remarks>
    /// <param name="prNumber">The pull request number to evaluate. Must be positive.</param>
    /// <param name="repo">Optional <c>owner/repo</c> slug forwarded to <c>gh --repo</c>. Defaults to the slug derived from the current checkout's <c>origin</c> remote.</param>
    /// <param name="minCommits">Minimum number of commits the PR's head branch must have beyond base. Defaults to 1. Exposed for forward flexibility; no current workflow tunes it.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("check-evidence-floor")]
    public async Task<int> CheckEvidenceFloor(
        [Argument] int prNumber,
        string repo = "",
        int minCommits = 1,
        CancellationToken ct = default)
    {
        if (prNumber <= 0)
        {
            EmitFloorError(
                prNumber, "gh_failed",
                $"prNumber must be positive (got {prNumber})");
            return ExitCodes.Success;
        }
        if (minCommits < 0)
        {
            EmitFloorError(
                prNumber, "gh_failed",
                $"minCommits must be non-negative (got {minCommits})");
            return ExitCodes.Success;
        }

        try
        {
            var slug = !string.IsNullOrWhiteSpace(repo)
                ? repo.Trim()
                : await TryResolveSlugAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(slug))
            {
                EmitFloorError(
                    prNumber, "gh_failed",
                    "Could not resolve repo slug from origin remote (pass --repo owner/repo)");
                return ExitCodes.Success;
            }

            var read = await gh.GetPullRequestEvidenceFloorAsync(slug, prNumber, ct)
                .ConfigureAwait(false);

            switch (read.Outcome)
            {
                case GhEvidenceFloorOutcome.PrNotFound:
                    EmitFloorError(
                        prNumber, "pr_not_found",
                        string.IsNullOrEmpty(read.Detail)
                            ? $"PR #{prNumber} not found on {slug}"
                            : read.Detail);
                    return ExitCodes.Success;

                case GhEvidenceFloorOutcome.GhFailed:
                    EmitFloorError(
                        prNumber, "gh_failed",
                        string.IsNullOrEmpty(read.Detail)
                            ? "gh pr view failed without detail"
                            : read.Detail);
                    return ExitCodes.Success;

                case GhEvidenceFloorOutcome.Found:
                default:
                    break;
            }

            var trimmedBody = (read.Body ?? string.Empty).Trim();
            var bodyLength = trimmedBody.Length;
            var commitCount = read.CommitCount;

            // Violations are listed in declaration order: commits first,
            // body second. Workflow templates render them in this order
            // so operators see the most "agent crashed" signal first.
            var violations = new List<string>(2);
            if (commitCount < minCommits)
            {
                violations.Add("no_commits");
            }
            if (bodyLength == 0)
            {
                violations.Add("empty_body");
            }

            EmitFloor(new PrCheckEvidenceFloorResult
            {
                Success = true,
                PrNumber = prNumber,
                CommitCount = commitCount,
                BodyLength = bodyLength,
                PassesFloor = violations.Count == 0,
                Violations = violations,
                ErrorCode = null,
                ErrorMessage = null,
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitFloorError(prNumber, "gh_failed", ex.Message);
            return ExitCodes.Success;
        }
    }

    private static void EmitFloor(PrCheckEvidenceFloorResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrCheckEvidenceFloorResult));

    private static void EmitFloorError(int prNumber, string errorCode, string errorMessage)
    {
        EmitFloor(new PrCheckEvidenceFloorResult
        {
            Success = false,
            PrNumber = prNumber,
            CommitCount = 0,
            BodyLength = 0,
            PassesFloor = false,
            Violations = Array.Empty<string>(),
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        });
    }
}
