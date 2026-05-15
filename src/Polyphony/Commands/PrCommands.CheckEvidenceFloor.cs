using System.Net;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc.Observers;

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
    /// <para><b>Repo identity resolution.</b> Honours the canonical
    /// override flags (<c>--platform</c>, <c>--organization</c>,
    /// <c>--project</c>, <c>--repository-override</c>) via
    /// <see cref="RepoIdentityResolver"/>; falls back to the legacy
    /// <c>--repo</c> github slug shortcut for backward compat. When all
    /// overrides are empty, the resolver parses the origin URL.</para>
    ///
    /// <para><b>Violations.</b> Stable machine-readable rule names,
    /// listed in declaration order: <c>no_commits</c> when
    /// <c>commit_count &lt; minCommits</c>, <c>empty_body</c> when the
    /// trimmed body length is zero. Both can fire on the same PR.</para>
    ///
    /// <para><b>Cross-platform.</b> Branches on resolved
    /// <see cref="RepoIdentity"/>: GitHub origins call
    /// <see cref="IGhClient.GetPullRequestEvidenceFloorAsync"/>, ADO
    /// origins call <see cref="IAdoClient.GetPullRequestEvidenceFloorAsync"/>.
    /// Both wire to the same neutral envelope.</para>
    /// </remarks>
    /// <param name="prNumber">The pull request number to evaluate. Must be positive.</param>
    /// <param name="repo">Optional <c>owner/repo</c> github slug forwarded to <c>gh --repo</c>. Backward-compat shortcut for github origins; ignored on ADO. Defaults to the slug derived from origin.</param>
    /// <param name="minCommits">Minimum number of commits the PR's head branch must have beyond base. Defaults to 1.</param>
    /// <param name="platform">Platform override: <c>github</c> or <c>ado</c>. Empty for origin-URL auto-detect.</param>
    /// <param name="organization">ADO organization (required when <c>--platform ado</c>).</param>
    /// <param name="project">ADO project (required when <c>--platform ado</c>).</param>
    /// <param name="repositoryOverride">Repository override. For <c>--platform ado</c>: the repo name. For <c>--platform github</c>: <c>owner/repo</c> slug.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("check-evidence-floor")]
    [VerbResult(typeof(PrCheckEvidenceFloorResult))]
    public async Task<int> CheckEvidenceFloor(
        [Argument] int prNumber = RequiredInput.MissingInt,
        string repo = "",
        int minCommits = 1,
        string platform = "",
        string organization = "",
        string project = "",
        string repositoryOverride = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr check-evidence-floor",
            ("pr-number", prNumber == RequiredInput.MissingInt)) is { } halt)
            return halt;

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
            // Resolve identity — honour explicit overrides first, fall back to
            // the legacy --repo github-slug shortcut, then origin parsing.
            RepoIdentity? identity;
            if (!string.IsNullOrWhiteSpace(platform)
                || !string.IsNullOrWhiteSpace(repositoryOverride)
                || !string.IsNullOrWhiteSpace(organization)
                || !string.IsNullOrWhiteSpace(project))
            {
                var resolved = await repoIdentityResolver
                    .ResolveAsync(platform, organization, project, repositoryOverride, ct)
                    .ConfigureAwait(false);
                identity = resolved.Identity;
                if (identity is null)
                {
                    EmitFloorError(prNumber, "gh_failed",
                        resolved.Error ?? "Could not resolve repo identity from overrides");
                    return ExitCodes.Success;
                }
            }
            else if (!string.IsNullOrWhiteSpace(repo))
            {
                var resolved = await repoIdentityResolver
                    .ResolveAsync("github", "", "", repo, ct)
                    .ConfigureAwait(false);
                identity = resolved.Identity;
                if (identity is null)
                {
                    EmitFloorError(prNumber, "gh_failed",
                        resolved.Error ?? $"--repo '{repo}' is not a valid owner/repo slug");
                    return ExitCodes.Success;
                }
            }
            else
            {
                var resolved = await repoIdentityResolver
                    .ResolveAsync("", "", "", "", ct)
                    .ConfigureAwait(false);
                identity = resolved.Identity;
                if (identity is null)
                {
                    EmitFloorError(prNumber, "gh_failed",
                        "Could not resolve repo slug from origin remote (pass --repo owner/repo or --platform/--organization/--project/--repository-override)");
                    return ExitCodes.Success;
                }
            }

            switch (identity)
            {
                case RepoIdentity.GitHubRepo githubRepo:
                    await CheckEvidenceFloorGithubAsync(prNumber, githubRepo.Slug, minCommits, ct)
                        .ConfigureAwait(false);
                    return ExitCodes.Success;

                case RepoIdentity.AdoRepo adoRepo:
                    await CheckEvidenceFloorAdoAsync(prNumber, adoRepo, minCommits, ct)
                        .ConfigureAwait(false);
                    return ExitCodes.Success;

                default:
                    EmitFloorError(prNumber, "gh_failed",
                        $"Unsupported repo identity variant: {identity.GetType().Name}");
                    return ExitCodes.Success;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitFloorError(prNumber, "gh_failed", ex.Message);
            return ExitCodes.Success;
        }
    }

    private async Task CheckEvidenceFloorGithubAsync(int prNumber, string slug, int minCommits, CancellationToken ct)
    {
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
                return;

            case GhEvidenceFloorOutcome.GhFailed:
                EmitFloorError(
                    prNumber, "gh_failed",
                    string.IsNullOrEmpty(read.Detail)
                        ? "gh pr view failed without detail"
                        : read.Detail);
                return;

            case GhEvidenceFloorOutcome.Found:
            default:
                EmitFloorClassification(prNumber, read.CommitCount, read.Body, minCommits);
                return;
        }
    }

    private async Task CheckEvidenceFloorAdoAsync(int prNumber, RepoIdentity.AdoRepo identity, int minCommits, CancellationToken ct)
    {
        if (ado is null)
        {
            EmitFloorError(prNumber, "gh_failed",
                "IAdoClient is not configured but identity resolved to AdoRepo");
            return;
        }

        AdoEvidenceFloorRead read;
        try
        {
            read = await ado.GetPullRequestEvidenceFloorAsync(
                identity.Organization, identity.Project, identity.Repository, prNumber, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            EmitFloorError(prNumber, "gh_failed", ex.Message);
            return;
        }
        catch (Exception ex)
        {
            EmitFloorError(prNumber, "gh_failed", ex.Message);
            return;
        }

        switch (read.Outcome)
        {
            case AdoEvidenceFloorOutcome.PrNotFound:
                EmitFloorError(
                    prNumber, "pr_not_found",
                    string.IsNullOrEmpty(read.Detail)
                        ? $"PR #{prNumber} not found in {identity.Organization}/{identity.Project}/{identity.Repository}"
                        : read.Detail);
                return;

            case AdoEvidenceFloorOutcome.AdoFailed:
                EmitFloorError(
                    prNumber, "gh_failed",
                    string.IsNullOrEmpty(read.Detail)
                        ? "ADO PR detail/commits call failed without detail"
                        : read.Detail);
                return;

            case AdoEvidenceFloorOutcome.Found:
            default:
                EmitFloorClassification(prNumber, read.CommitCount, read.Body, minCommits);
                return;
        }
    }

    private static void EmitFloorClassification(int prNumber, int commitCount, string? body, int minCommits)
    {
        var trimmedBody = (body ?? string.Empty).Trim();
        var bodyLength = trimmedBody.Length;

        // Violations are listed in declaration order: commits first, body second.
        var violations = new List<string>(2);
        if (commitCount < minCommits) violations.Add("no_commits");
        if (bodyLength == 0) violations.Add("empty_body");

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
