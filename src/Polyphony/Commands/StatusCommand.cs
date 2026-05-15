using System.Reflection;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;
using Polyphony.Manifest;
using Polyphony.Tagging;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// <c>polyphony status</c> — periodic dashboard snapshot for a single apex.
/// Composes the ADO cache, the run manifest, and a best-effort gh PR query
/// into a unified JSON envelope plus a human-readable headline. Designed
/// for polling (e.g. a dashboard widget after each conductor event).
///
/// <para>Routing-style verb: ALWAYS exits 0. Failure modes (work item
/// missing, manifest unparseable, gh hung) are surfaced through per-section
/// <c>error</c> fields and the <see cref="StatusResult.Warnings"/> array,
/// not via process exit code. The dashboard cares about cross-signal
/// coherence — a wedged gh shouldn't blank the whole report.</para>
///
/// <para>Cross-signal warnings caught today:</para>
/// <list type="bullet">
///   <item><c>apex_not_in_scope</c> — work item missing the
///     <see cref="PolyphonyTags.InScope"/> / <see cref="PolyphonyTags.Root"/> tag.</item>
///   <item><c>apex_not_root</c> — work item in-scope but missing
///     <see cref="PolyphonyTags.Root"/>; status was likely invoked on a
///     descendant by mistake.</item>
///   <item><c>planned_tag_zero_children</c> — apex carries
///     <see cref="PolyphonyTags.Planned"/> but has zero ADO children.
///     This is the AB#3064 false-satisfied dogfood bug — the seeder
///     stamped the tag with empty input. Now caught at lint time by
///     PR #225's strict seed-children, but the warning stays as a
///     belt-and-braces dashboard signal.</item>
///   <item><c>manifest_missing</c> — no <c>.polyphony/run.yaml</c>; either
///     the run hasn't started or the working directory isn't the run root.</item>
///   <item><c>feature_pr_unmerged_progress</c> — manifest records merged
///     plan PRs but no feature PR exists or it's still open. Honest
///     signal that work has happened but hasn't been promoted.</item>
/// </list>
/// </summary>
[VerbGroup("")]
public sealed class StatusCommand(
    IWorkItemRepository repository,
    Sdlc.Observers.RepoIdentityResolver repoIdentityResolver,
    Sdlc.Observers.PullRequestReader pullRequestReader)
{
    /// <summary>Compose a periodic status snapshot for an apex work item.</summary>
    /// <param name="apex">Apex (focus) work item ID.</param>
    /// <param name="repoSlug">Owner/repo slug (GitHub) for the feature-PR lookup.
    /// When empty, derived from the <c>origin</c> remote (GitHub or ADO);
    /// when no identity can be derived, the feature_pr section reports
    /// <c>error: no_slug</c>. Override via <c>--platform/--organization/
    /// --project/--repository-override</c> for ADO repos.</param>
    /// <param name="manifestPath">Run manifest path. Defaults to <c>.polyphony/run.yaml</c>.</param>
    /// <param name="platform">Optional platform override (<c>github</c>|<c>ado</c>); defaults to origin URL detection.</param>
    /// <param name="organization">ADO organization (required when <paramref name="platform"/> is <c>ado</c>).</param>
    /// <param name="project">ADO project (required when <paramref name="platform"/> is <c>ado</c>).</param>
    /// <param name="repositoryOverride">Repository override (GitHub <c>owner/repo</c> or ADO repo name).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("status")]
    [VerbResult(typeof(StatusResult))]
    public async Task<int> Status(
        int apex = RequiredInput.MissingInt,
        string repoSlug = "",
        string manifestPath = RunManifestStore.DefaultRelativePath,
        string platform = "",
        string organization = "",
        string project = "",
        string repositoryOverride = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("status",
            ("--apex", apex == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var ado = await ReadAdoSectionAsync(apex, ct).ConfigureAwait(false);
        var manifest = ReadManifestSection(manifestPath);
        var featurePr = await ReadFeaturePrSectionAsync(
            apex, repoSlug, platform, organization, project, repositoryOverride, ct).ConfigureAwait(false);
        var binary = ReadBinarySection();

        var warnings = ComputeWarnings(ado, manifest, featurePr);
        var (headline, nextAction) = ComputeHeadline(apex, ado, manifest, featurePr, warnings);

        var result = new StatusResult
        {
            ApexId = apex,
            Ado = ado,
            Manifest = manifest,
            FeaturePr = featurePr,
            Binary = binary,
            Warnings = warnings,
            Headline = headline,
            NextAction = nextAction,
        };

        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.StatusResult));
        return ExitCodes.Success;
    }

    private async Task<StatusAdoSection> ReadAdoSectionAsync(int apex, CancellationToken ct)
    {
        var item = await repository.GetByIdAsync(apex, ct).ConfigureAwait(false);
        if (item is null)
        {
            return new StatusAdoSection
            {
                Found = false,
                Tags = [],
                InScope = false,
                IsRoot = false,
                HasPlannedTag = false,
                ChildrenCount = 0,
                Error = $"Work item {apex} not found in twig cache",
            };
        }

        item.Fields.TryGetValue("System.Tags", out var rawTags);
        var tags = TagSet.Parse(rawTags);

        int childrenCount;
        try
        {
            var children = await repository.GetChildrenAsync(apex, ct).ConfigureAwait(false);
            childrenCount = children.Count;
        }
        catch
        {
            childrenCount = 0;
        }

        return new StatusAdoSection
        {
            Found = true,
            Type = item.Type.Value,
            State = item.State,
            Title = item.Title,
            Tags = tags.ToArray(),
            InScope = PolyphonyTags.IsInScope(tags),
            IsRoot = PolyphonyTags.IsRoot(tags),
            HasPlannedTag = tags.Contains(PolyphonyTags.Planned),
            ChildrenCount = childrenCount,
        };
    }

    private static StatusManifestSection ReadManifestSection(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return new StatusManifestSection
            {
                Exists = false,
                Path = manifestPath,
            };
        }

        try
        {
            var manifest = RunManifestStore.LoadOrThrow(manifestPath);
            int? rootGen = manifest.PlanGenerations is { Count: > 0 } pg
                && pg.TryGetValue("root", out var v) ? v : null;
            return new StatusManifestSection
            {
                Exists = true,
                Path = manifestPath,
                FeatureBranch = $"feature/{manifest.RootId}",
                PlanGenerationsRoot = rootGen,
                MergedPlanPrsCount = manifest.MergedPlanPrs?.Count ?? 0,
                MergeGroupsCount = manifest.MergeGroups?.Count ?? 0,
            };
        }
        catch (Exception ex)
        {
            return new StatusManifestSection
            {
                Exists = true,
                Path = manifestPath,
                Error = ex.Message,
            };
        }
    }

    private async Task<StatusFeaturePrSection> ReadFeaturePrSectionAsync(
        int apex, string repoSlug,
        string platform, string organization, string project, string repositoryOverride,
        CancellationToken ct)
    {
        // If the operator passed an explicit slug, honor it as a GitHub slug
        // (back-compat: this verb predates --platform). Otherwise resolve from
        // origin URL or platform overrides via RepoIdentityResolver.
        Sdlc.Observers.RepoIdentity? identity;
        if (!string.IsNullOrEmpty(repoSlug))
        {
            var slugParts = repoSlug.Split('/');
            identity = slugParts.Length == 2
                ? new Sdlc.Observers.RepoIdentity.GitHubRepo(slugParts[0], slugParts[1])
                : null;
        }
        else
        {
            try
            {
                var resolved = await repoIdentityResolver
                    .ResolveAsync(platform, organization, project, repositoryOverride, ct)
                    .ConfigureAwait(false);
                identity = resolved.Identity;
            }
            catch
            {
                identity = null;
            }
        }

        if (identity is null)
        {
            return new StatusFeaturePrSection
            {
                Exists = false,
                Error = "no_slug",
            };
        }

        var headBranch = $"feature/{apex}";
        try
        {
            // Look at all states (open/merged/closed) so a merged feature PR is
            // visible. gh's --state default is `open`, which would hide a merged
            // PR and trip the feature_pr_unmerged_progress warning incorrectly.
            var prs = await pullRequestReader.ListByHeadAsync(
                identity, headBranch: headBranch, state: "all", limit: 5, ct).ConfigureAwait(false);

            var match = prs.FirstOrDefault(p => string.Equals(
                p.HeadRefName, headBranch, StringComparison.Ordinal));
            if (match is null)
            {
                return new StatusFeaturePrSection { Exists = false };
            }

            var state = match.MergedAt.HasValue ? "MERGED" : "OPEN";
            return new StatusFeaturePrSection
            {
                Exists = true,
                Number = match.Number,
                Url = match.Url,
                State = state,
                MergedAt = match.MergedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        catch (Exception ex)
        {
            return new StatusFeaturePrSection
            {
                Exists = false,
                Error = ex.Message,
            };
        }
    }

    private static StatusBinarySection ReadBinarySection()
    {
        var asm = typeof(StatusCommand).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        var version = asm.GetName().Version?.ToString() ?? "unknown";
        string? location = null;
        try
        {
            location = Environment.ProcessPath;
        }
        catch
        {
            // Best-effort; never poison the result.
        }

        return new StatusBinarySection
        {
            Version = version,
            InformationalVersion = informational,
            Location = location,
        };
    }

    private static IReadOnlyList<StatusWarning> ComputeWarnings(
        StatusAdoSection ado,
        StatusManifestSection manifest,
        StatusFeaturePrSection featurePr)
    {
        var warnings = new List<StatusWarning>();

        if (ado.Found)
        {
            if (!ado.InScope)
            {
                warnings.Add(new StatusWarning
                {
                    Code = "apex_not_in_scope",
                    Message = "Work item is missing the polyphony / polyphony:root tag.",
                });
            }
            else if (!ado.IsRoot)
            {
                warnings.Add(new StatusWarning
                {
                    Code = "apex_not_root",
                    Message = "Work item carries the polyphony tag but not polyphony:root. status was probably invoked on a descendant.",
                });
            }

            if (ado.HasPlannedTag && ado.ChildrenCount == 0)
            {
                warnings.Add(new StatusWarning
                {
                    Code = "planned_tag_zero_children",
                    Message = "polyphony:planned tag is set but the work item has no ADO children. Closed-loop §3.4(a) requires apex_facets in the plan front-matter when the apex is genuinely indivisible; otherwise this is a false-satisfied bug.",
                });
            }
        }

        if (!manifest.Exists)
        {
            warnings.Add(new StatusWarning
            {
                Code = "manifest_missing",
                Message = $"Run manifest not found at {manifest.Path ?? RunManifestStore.DefaultRelativePath}. Either the run has not started or the working directory is not the run root.",
            });
        }

        if (manifest.Exists
            && manifest.MergedPlanPrsCount is > 0
            && (!featurePr.Exists || string.Equals(featurePr.State, "OPEN", StringComparison.Ordinal)))
        {
            warnings.Add(new StatusWarning
            {
                Code = "feature_pr_unmerged_progress",
                Message = "Manifest records merged plan PRs but the feature PR is not yet merged. Implementation work has happened but is not promoted to main.",
            });
        }

        return warnings;
    }

    private static (string Headline, string? NextAction) ComputeHeadline(
        int apex,
        StatusAdoSection ado,
        StatusManifestSection manifest,
        StatusFeaturePrSection featurePr,
        IReadOnlyList<StatusWarning> warnings)
    {
        if (!ado.Found)
        {
            return ($"apex {apex}: not found in twig cache",
                    "Run `twig sync` to refresh the cache, or verify the work item ID.");
        }

        // Most-actionable warning takes the headline.
        var plannedZero = warnings.FirstOrDefault(w => w.Code == "planned_tag_zero_children");
        if (plannedZero is not null)
        {
            return ($"apex {apex}: planned but no children — false-satisfied bug",
                    $"Inspect the plan: `cat plans/plan-{apex}.md`. If children are declared in prose only, re-run plan-level so the architect emits structured `output.children`. If genuinely indivisible, declare `apex_facets: [implementable]` in plan front-matter.");
        }

        var notInScope = warnings.FirstOrDefault(w => w.Code == "apex_not_in_scope");
        if (notInScope is not null)
        {
            return ($"apex {apex}: not in polyphony scope (tag missing)",
                    $"polyphony root declare --work-item {apex}");
        }

        var unmergedProgress = warnings.FirstOrDefault(w => w.Code == "feature_pr_unmerged_progress");
        if (unmergedProgress is not null)
        {
            var prSegment = featurePr is { Exists: true, Number: { } n }
                ? $"feature PR #{n} is open"
                : "no feature PR exists";
            return ($"apex {apex}: progress recorded ({manifest.MergedPlanPrsCount} plan PR(s) merged) but {prSegment}",
                    "Check `gh pr list --state open` for an open feature PR; ship it once impl PRs are merged.");
        }

        if (featurePr is { Exists: true, State: "MERGED" })
        {
            return ($"apex {apex}: feature PR #{featurePr.Number} merged",
                    "Verify the ADO work item has advanced to its terminal state. The apex-driver's `close_mark_satisfied` step transitions the state via `polyphony validate --event item_satisfied` + `twig state` — if the state didn't advance, the run likely exited before reaching that step. Either re-run `apex-driver` or set the state directly via `twig state Done`.");
        }

        if (!manifest.Exists)
        {
            return ($"apex {apex}: manifest not initialised (state={ado.State})",
                    $"conductor run apex-driver@polyphony --input apex_id={apex}");
        }

        return ($"apex {apex}: in flight (state={ado.State}, children={ado.ChildrenCount}, merged plan PRs={manifest.MergedPlanPrsCount ?? 0})",
                "Run `polyphony state next-ready --work-item {apex}` for the next dispatchable requirement.");
    }
}
