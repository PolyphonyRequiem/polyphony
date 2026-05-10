using System.Text.Json;
using System.Text.RegularExpressions;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Configuration;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Paths;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// PR-lifecycle verbs (<c>polyphony pr ...</c>). Replaces the deterministic
/// PowerShell scripts invoked from <c>feature-pr.yaml</c> and
/// <c>github-pr.yaml</c>:
/// <list type="bullet">
///   <item><c>scripts/feature-pr-creator.ps1</c> → <see cref="CreateFeaturePr"/></item>
/// </list>
/// (<c>scripts/invoke-gh.ps1</c> and <c>scripts/resolve-gh-token.ps1</c>
/// have been collapsed into the internal <see cref="GhClient"/> helper —
/// they are NOT exposed as verbs.)
/// </summary>
// Single ctor (ConsoleAppFramework CAF011 — Add<T> rejects multiple ctors).
// IAdoClient is injected as an optional dependency: production runs get the
// DI-resolved instance (registered in PolyphonyServiceRegistration); GitHub-only
// tests pass null and never exercise the ADO leg.
[VerbGroup("pr")]
public sealed partial class PrCommands(
    IGitClient git,
    IGhClient gh,
    ITwigClient twig,
    IWorkItemRepository repository,
    ProcessConfig processConfig,
    Polyphony.Locking.RunLockStore lockStore,
    Polyphony.Locking.RunLockPathResolver lockPathResolver,
    PolyphonyStatePaths statePaths,
    IAdoClient? ado = null)
{
    private static readonly Regex PullUrlRegex =
        new(@"/pull/(\d+)", RegexOptions.Compiled);
    private static readonly Regex GitHubSlugRegex =
        new(@"github\.com[:/]([^/]+/[^/]+?)(?:\.git)?(?:[/?#].*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Create the feature PR closing a root work item. Reuses an existing
    /// open PR for the same head/base pair instead of creating a duplicate.
    /// </summary>
    /// <param name="workItem">ADO work item ID of the root (Epic/Feature) item.</param>
    /// <param name="featureBranch">Source branch containing all merged PG work.</param>
    /// <param name="targetBranch">Target branch (typically <c>main</c>).</param>
    /// <param name="title">Optional PR title; auto-generated from the work item when empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("create-feature-pr")]
    [VerbResult(typeof(PrCreateFeatureResult))]
    public async Task<int> CreateFeaturePr(
        int workItem = RequiredInput.MissingInt,
        string featureBranch = "",
        string targetBranch = "",
        string title = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr create-feature-pr",
            ("--work-item", workItem == RequiredInput.MissingInt),
            ("--feature-branch", string.IsNullOrEmpty(featureBranch)),
            ("--target-branch", string.IsNullOrEmpty(targetBranch))) is { } halt)
            return halt;

        if (string.IsNullOrWhiteSpace(featureBranch) || string.IsNullOrWhiteSpace(targetBranch))
        {
            EmitError("featureBranch and targetBranch are required");
            return ExitCodes.ConfigError;
        }

        try
        {
            // Optional consistency check against polyphony's own routing hint.
            try
            {
                var item = await repository.GetByIdAsync(workItem, ct).ConfigureAwait(false);
                if (item is not null)
                {
                    var hint = BranchNameResolver.Resolve(processConfig, item);
                    if (!string.IsNullOrEmpty(hint?.FeatureBranch)
                        && !string.Equals(hint.FeatureBranch, featureBranch, StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine(
                            $"WARNING: workspace_hint feature_branch '{hint.FeatureBranch}' differs from supplied "
                            + $"FeatureBranch '{featureBranch}'");
                    }
                }
            }
            catch { /* non-fatal — branch validation is best-effort */ }

            var heads = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{featureBranch}", ct).ConfigureAwait(false);
            if (heads.Count == 0)
            {
                EmitError($"Feature branch '{featureBranch}' does not exist on remote");
                return ExitCodes.RoutingFailure;
            }

            var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(slug))
            {
                EmitError("Could not resolve repo slug from origin remote");
                return ExitCodes.RoutingFailure;
            }

            var prTitle = string.IsNullOrWhiteSpace(title)
                ? await ResolvePrTitleAsync(workItem, ct).ConfigureAwait(false)
                : title;
            var body = await BuildPrBodyAsync(workItem, featureBranch, targetBranch, ct).ConfigureAwait(false);

            // Reuse an existing open PR for the same head/base pair if one exists.
            var existing = await gh.ListPullRequestsAsync(
                slug,
                new PrListFilters(Head: featureBranch, Base: targetBranch, State: "open", Limit: 1),
                ct).ConfigureAwait(false);
            if (existing.Count > 0)
            {
                var found = existing[0];
                var reuseResult = new PrCreateFeatureResult
                {
                    PrNumber = found.Number,
                    PrUrl = found.Url ?? "",
                    Title = prTitle,
                    DescriptionSummary = "Reusing existing open feature PR",
                    Created = false,
                };
                Emit(reuseResult);
                return ExitCodes.Success;
            }

            // Create the PR.
            var url = await gh.CreatePullRequestAsync(slug, targetBranch, featureBranch, prTitle, body, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(url))
            {
                EmitError("gh pr create failed — no URL returned");
                return ExitCodes.RoutingFailure;
            }

            var trimmedUrl = url.Trim();
            var prNumber = ExtractPrNumber(trimmedUrl);

            var createdResult = new PrCreateFeatureResult
            {
                PrNumber = prNumber,
                PrUrl = trimmedUrl,
                Title = prTitle,
                DescriptionSummary = $"Feature PR created: {featureBranch} -> {targetBranch}",
                Created = true,
            };
            Emit(createdResult);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitError(ex.Message);
            return ExitCodes.RoutingFailure;
        }
    }

    private async Task<string> ResolvePrTitleAsync(int workItem, CancellationToken ct)
    {
        var fallback = $"feat: deliver work item #{workItem} AB#{workItem}";
        try
        {
            var tree = await twig.ShowTreeAsync(workItem, ct).ConfigureAwait(false);
            var title = tree?["title"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(title) ? fallback : $"feat: {title} AB#{workItem}";
        }
        catch
        {
            return fallback;
        }
    }

    private async Task<string> BuildPrBodyAsync(
        int workItem, string featureBranch, string targetBranch, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("## Feature PR for Work Item #").Append(workItem).Append("\n\n");
        sb.Append("Delivers all PR group work from `").Append(featureBranch)
          .Append("` into `").Append(targetBranch).Append("`.\n");

        try
        {
            var tree = await twig.ShowTreeAsync(workItem, ct).ConfigureAwait(false);
            if (tree is not null)
            {
                sb.Append("\n### Work Item Hierarchy\n\n```json\n");
                sb.Append(tree.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                sb.Append("\n```\n");
            }
        }
        catch { /* hierarchy is optional — body is still useful without it */ }

        return sb.ToString();
    }

    private async Task<string> TryResolveSlugAsync(CancellationToken ct)
    {
        try
        {
            var url = await git.GetRemoteUrlAsync("origin", ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url)) return "";
            var match = GitHubSlugRegex.Match(url);
            return match.Success ? match.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    private static int ExtractPrNumber(string url)
    {
        var match = PullUrlRegex.Match(url);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
    }

    private static void Emit(PrCreateFeatureResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrCreateFeatureResult));

    private static void EmitError(string message)
    {
        var result = new PrCreateFeatureResult
        {
            PrNumber = 0,
            PrUrl = "",
            Title = "",
            DescriptionSummary = $"Error: {message}",
            Created = false,
            Error = message,
        };
        Emit(result);
    }
}
