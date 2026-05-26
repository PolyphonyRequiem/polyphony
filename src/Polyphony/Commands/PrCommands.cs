using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Configuration;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Paths;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;
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
    Polyphony.Sdlc.Observers.RepoIdentityResolver repoIdentityResolver,
    RunContext runContext,
    JournaledActionDecorator decorator,
    IAdoClient? ado = null)
{
    private readonly RunContext _runContext = runContext;
    private readonly JournaledActionDecorator _journalDecorator = decorator;

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
    [JournaledAction(Action = "pr_create_feature_pr")]
    [MutatesResource(ResourceKind.GitHubPr)]
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

        PrCreateFeaturePrPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation(
                "pr_create_feature_pr",
                BranchPairJournalTarget(featureBranch, targetBranch),
                workItem,
                workItem),
            async innerCt =>
            {
                try
                {
                    // Optional consistency check against polyphony's own routing hint.
                    try
                    {
                        var item = await repository.GetByIdAsync(workItem, innerCt).ConfigureAwait(false);
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
                    catch { }

                    var heads = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{featureBranch}", innerCt).ConfigureAwait(false);
                    if (heads.Count == 0)
                    {
                        var error = $"Feature branch '{featureBranch}' does not exist on remote";
                        payload = new PrCreateFeaturePrPayload
                        {
                            WorkItemId = workItem,
                            FeatureBranch = featureBranch,
                            TargetBranch = targetBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitError(error);
                        return ExitCodes.RoutingFailure;
                    }

                    var slug = await TryResolveSlugAsync(innerCt).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(slug))
                    {
                        const string error = "Could not resolve repo slug from origin remote";
                        payload = new PrCreateFeaturePrPayload
                        {
                            WorkItemId = workItem,
                            FeatureBranch = featureBranch,
                            TargetBranch = targetBranch,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitError(error);
                        return ExitCodes.RoutingFailure;
                    }

                    var prTitle = string.IsNullOrWhiteSpace(title)
                        ? await ResolvePrTitleAsync(workItem, innerCt).ConfigureAwait(false)
                        : title;
                    var body = await BuildPrBodyAsync(workItem, featureBranch, targetBranch, innerCt).ConfigureAwait(false);

                    var existing = await gh.ListPullRequestsAsync(
                        slug,
                        new PrListFilters(Head: featureBranch, Base: targetBranch, State: "open", Limit: 1),
                        innerCt).ConfigureAwait(false);
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
                        payload = new PrCreateFeaturePrPayload
                        {
                            WorkItemId = workItem,
                            FeatureBranch = featureBranch,
                            TargetBranch = targetBranch,
                            RepoSlug = slug,
                            PrNumber = reuseResult.PrNumber,
                            PrUrl = reuseResult.PrUrl,
                            Title = reuseResult.Title,
                            ResultAction = "reused_existing_pr",
                            Succeeded = true,
                            WasMutated = false,
                        };
                        Emit(reuseResult);
                        return ExitCodes.Success;
                    }

                    var url = await gh.CreatePullRequestAsync(slug, targetBranch, featureBranch, prTitle, body, innerCt)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        const string error = "gh pr create failed — no URL returned";
                        payload = new PrCreateFeaturePrPayload
                        {
                            WorkItemId = workItem,
                            FeatureBranch = featureBranch,
                            TargetBranch = targetBranch,
                            RepoSlug = slug,
                            Title = prTitle,
                            ResultAction = "error",
                            Succeeded = false,
                            WasMutated = false,
                            Error = error,
                        };
                        EmitError(error);
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
                    payload = new PrCreateFeaturePrPayload
                    {
                        WorkItemId = workItem,
                        FeatureBranch = featureBranch,
                        TargetBranch = targetBranch,
                        RepoSlug = slug,
                        PrNumber = createdResult.PrNumber,
                        PrUrl = createdResult.PrUrl,
                        Title = createdResult.Title,
                        ResultAction = "created",
                        Succeeded = true,
                        WasMutated = true,
                    };
                    Emit(createdResult);
                    return ExitCodes.Success;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    payload = new PrCreateFeaturePrPayload
                    {
                        WorkItemId = workItem,
                        FeatureBranch = featureBranch,
                        TargetBranch = targetBranch,
                        ResultAction = "error",
                        Succeeded = false,
                        WasMutated = false,
                        Error = ex.Message,
                    };
                    EmitError(ex.Message);
                    return ExitCodes.RoutingFailure;
                }
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.PrCreateFeaturePrPayload),
            effectsSelector: _ => SelectCreateFeaturePrEffects(payload),
            ct: ct).ConfigureAwait(false);
    }

    private async Task<string> ResolvePrTitleAsync(int workItem, CancellationToken ct)
    {
        var fallback = $"feat: deliver work item #{workItem} AB#{workItem}";
        try
        {
            var tree = await twig.ShowTreeAsync(workItem, ct).ConfigureAwait(false);
            // twig >= v0.81.0 tree root is {parentChain, focus, children, …};
            // the active item's title lives under `focus`.
            var title = tree?["focus"]?["title"]?.GetValue<string>();
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
        // Delegate to the shared resolver and assert the variant. ADO
        // origins (or any non-GitHub variant) return empty so existing
        // callers' "could not resolve slug" branch fires — defense in
        // depth against a workflow router mis-routing an ADO repo to a
        // GH-only verb.
        try
        {
            var resolved = await repoIdentityResolver
                .ResolveAsync("", "", "", "", ct).ConfigureAwait(false);
            return resolved.Identity is Polyphony.Sdlc.Observers.RepoIdentity.GitHubRepo gh
                ? gh.Slug
                : "";
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

    private JournaledActionInvocation CreateJournalInvocation(
        string action,
        string target,
        int? rootId = null,
        int? workItemId = null,
        string? payloadJson = null)
        => new()
        {
            RunId = _runContext.RunId,
            RootId = rootId,
            WorkItemId = workItemId,
            Action = action,
            Target = target,
            PayloadJson = payloadJson,
        };

    private static JournalOutcome SelectJournalOutcome(int exitCode, bool succeeded, bool wasMutated)
    {
        if (exitCode != ExitCodes.Success || !succeeded)
        {
            return JournalOutcome.Failure;
        }

        return wasMutated ? JournalOutcome.Success : JournalOutcome.NoOp;
    }

    private static string? SerializePayload<TPayload>(TPayload? payload, JsonTypeInfo<TPayload> jsonTypeInfo)
        where TPayload : class
        => payload is null ? null : JsonSerializer.Serialize(payload, jsonTypeInfo);

    private static string PullRequestJournalTarget(int prNumber)
        => prNumber > 0 ? $"pr#{prNumber}" : string.Empty;

    private static string PullRequestJournalTarget(string prUrl, int prNumber)
        => !string.IsNullOrWhiteSpace(prUrl) ? prUrl : PullRequestJournalTarget(prNumber);

    private static string PullRequestCommentJournalTarget(string prUrl, int prNumber, int? commentId)
        => !string.IsNullOrWhiteSpace(prUrl)
            ? (commentId is > 0 ? $"{prUrl}#comment-{commentId.Value}" : prUrl)
            : PullRequestJournalTarget(prNumber);

    private static string BranchPairJournalTarget(string headBranch, string baseBranch)
        => string.IsNullOrWhiteSpace(headBranch) || string.IsNullOrWhiteSpace(baseBranch)
            ? string.Empty
            : $"{headBranch}->{baseBranch}";
}
