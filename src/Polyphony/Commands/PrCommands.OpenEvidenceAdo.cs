using System.Net;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Journal;
using Polyphony.Journal.Payloads;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Open (or reuse) the pull request that promotes an evidence branch
    /// into its parent feature branch (or main for orphan evidence) on
    /// Azure DevOps. ADO analogue of <c>polyphony pr open-evidence-pr</c>.
    ///
    /// <para>Default branch naming follows PR #2 (the evidence branch
    /// builder): non-orphan = <c>evidence/{root}-{workItem}</c> over
    /// <c>feature/{root}</c>; orphan = <c>evidence/{workItem}</c> over
    /// <c>main</c>. Per-PR overrides via <c>--head</c> / <c>--base-branch</c>
    /// are honored verbatim.</para>
    ///
    /// <para><b>Routing-style exit code</b> — always exits 0; consumers
    /// branch on <see cref="PrOpenEvidenceAdoResult.ErrorCode"/>.</para>
    /// </summary>
    /// <param name="organization">ADO organization name.</param>
    /// <param name="project">ADO project name.</param>
    /// <param name="repository">ADO repository identifier — GUID or name.</param>
    /// <param name="workItem">The actionable work-item id this evidence PR satisfies.</param>
    /// <param name="rootId">Optional run-root feature id. When omitted (or zero), defaults to <paramref name="workItem"/> (orphan evidence).</param>
    /// <param name="head">Optional head branch override.</param>
    /// <param name="baseBranch">Optional base branch override.</param>
    /// <param name="title">Optional PR title.</param>
    /// <param name="body">Optional PR body.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("open-evidence-ado")]
    [JournaledAction(Action = "pr_open_evidence_ado")]
    [MutatesResource(ResourceKind.AdoPr)]
    [VerbResult(typeof(PrOpenEvidenceAdoResult))]
    public async Task<int> OpenEvidenceAdo(
        string organization = "",
        string project = "",
        string repository = "",
        int workItem = RequiredInput.MissingInt,
        int rootId = 0,
        string head = "",
        string baseBranch = "",
        string title = "",
        string body = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr open-evidence-ado",
            ("--organization", string.IsNullOrEmpty(organization)),
            ("--project", string.IsNullOrEmpty(project)),
            ("--repository", string.IsNullOrEmpty(repository)),
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var slug = BuildAdoSlug(organization, project, repository);

        if (workItem <= 0)
        {
            EmitOpenEvidenceAdoError(workItem, rootId, organization, project, repository, slug,
                "invalid_argument", $"workItem must be positive (got {workItem})");
            return ExitCodes.Success;
        }
        if (rootId < 0)
        {
            EmitOpenEvidenceAdoError(workItem, rootId, organization, project, repository, slug,
                "invalid_argument", $"rootId must be non-negative (got {rootId})");
            return ExitCodes.Success;
        }

        var effectiveRoot = rootId == 0 ? workItem : rootId;
        var isOrphan = effectiveRoot == workItem;

        var headBranch = string.IsNullOrWhiteSpace(head)
            ? (isOrphan
                ? $"evidence/{workItem}"
                : $"evidence/{effectiveRoot}-{workItem}")
            : head;

        var resolvedBase = string.IsNullOrWhiteSpace(baseBranch)
            ? (isOrphan ? "main" : $"feature/{effectiveRoot}")
            : baseBranch;
        PrOpenEvidenceAdoPayload? payload = null;

        return await _journalDecorator.RunWithAsync(
            CreateJournalInvocation("pr_open_evidence_ado", BranchPairJournalTarget(headBranch, resolvedBase), effectiveRoot, workItem),
            async innerCt =>
            {
                var outcome = await OpenEvidenceAdoCoreAsync(
                    organization, project, repository, slug,
                    workItem, effectiveRoot,
                    headBranch, resolvedBase,
                    title, body, innerCt).ConfigureAwait(false);

                var result = new PrOpenEvidenceAdoResult
                {
                    WorkItemId = workItem,
                    RootId = effectiveRoot,
                    HeadBranch = outcome.HeadBranch,
                    BaseBranch = outcome.BaseBranch,
                    Organization = organization,
                    Project = project,
                    Repository = repository,
                    RepoSlug = slug,
                    PrNumber = outcome.PrNumber,
                    PrUrl = outcome.PrUrl,
                    Title = outcome.Title,
                    Created = outcome.Created,
                    ErrorCode = outcome.ErrorCode,
                    Error = outcome.Error,
                };
                payload = new PrOpenEvidenceAdoPayload
                {
                    WorkItemId = workItem,
                    RootId = effectiveRoot,
                    Organization = organization,
                    Project = project,
                    Repository = repository,
                    HeadBranch = result.HeadBranch,
                    BaseBranch = result.BaseBranch,
                    RepoSlug = slug,
                    PrNumber = result.PrNumber,
                    PrUrl = result.PrUrl,
                    Title = result.Title,
                    ResultAction = result.ErrorCode?.Length > 0 ? "error" : (result.Created ? "created" : "reused_existing_pr"),
                    Succeeded = string.IsNullOrEmpty(result.ErrorCode),
                    WasMutated = result.Created,
                    ErrorCode = result.ErrorCode,
                    Error = result.Error,
                };
                EmitOpenEvidenceAdo(result);
                return ExitCodes.Success;
            },
            outcomeSelector: exitCode => SelectJournalOutcome(
                exitCode,
                payload?.Succeeded ?? (exitCode == ExitCodes.Success),
                payload?.WasMutated ?? false),
            payloadSelector: _ => SerializePayload(payload, PolyphonyJsonContext.Default.PrOpenEvidenceAdoPayload),
            effectsSelector: _ => SelectOpenEvidenceAdoEffects(payload),
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared ADO logic for opening an evidence PR. Used by
    /// <see cref="OpenEvidenceAdo"/> (which wraps it with the legacy
    /// <see cref="PrOpenEvidenceAdoResult"/> envelope) and by
    /// <see cref="OpenEvidencePr"/>'s ADO branch (which wraps it with the
    /// unified <see cref="PrOpenEvidenceResult"/> envelope).
    /// </summary>
    internal async Task<EvidenceAdoOutcome> OpenEvidenceAdoCoreAsync(
        string organization,
        string project,
        string repository,
        string slug,
        int workItem,
        int effectiveRoot,
        string headBranch,
        string resolvedBase,
        string title,
        string body,
        CancellationToken ct)
    {
        if (ado is null)
        {
            return EvidenceAdoOutcome.Failure(headBranch, resolvedBase,
                "ado_failed", "IAdoClient is not configured");
        }

        try
        {
            var headRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{headBranch}", ct).ConfigureAwait(false);
            if (headRefs.Count == 0)
            {
                return EvidenceAdoOutcome.Failure(headBranch, resolvedBase,
                    "missing_head_branch", $"head branch '{headBranch}' does not exist on remote");
            }

            var baseRefs = await git.LsRemoteHeadsAsync("origin", $"refs/heads/{resolvedBase}", ct).ConfigureAwait(false);
            if (baseRefs.Count == 0)
            {
                return EvidenceAdoOutcome.Failure(headBranch, resolvedBase,
                    "missing_base_branch", $"base branch '{resolvedBase}' does not exist on remote");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return EvidenceAdoOutcome.Failure(headBranch, resolvedBase,
                "ado_failed", $"git ls-remote failed: {ex.Message}");
        }

        var prTitle = string.IsNullOrWhiteSpace(title)
            ? await ResolveEvidencePrTitleAsync(workItem, ct).ConfigureAwait(false)
            : title;
        var rawBody = string.IsNullOrWhiteSpace(body)
            ? BuildDefaultEvidenceBody(workItem, effectiveRoot, headBranch, resolvedBase)
            : body;
        // W6 (AB#3280): stamp the run-id marker on the first line.
        var prBody = PrBodyMarker.EnsureRunIdPrefix(rawBody, _runContext.RunId);

        try
        {
            // Includes Completed PRs so a retry after a successful merge reuses
            // the real merged PR rather than opening a degenerate no-op
            // duplicate (AB#3228). Active PRs win over Completed.
            var allPrs = await ado.ListPullRequestsAsync(
                organization, project, repository,
                AdoPullRequestStatus.All, sourceBranch: headBranch, ct).ConfigureAwait(false);

            if (allPrs is null)
            {
                return EvidenceAdoOutcome.Failure(headBranch, resolvedBase,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.");
            }

            var expectedTargetRef = "refs/heads/" + resolvedBase;
            AdoPullRequest? activeMatch = null;
            AdoPullRequest? completedMatch = null;
            foreach (var pr in allPrs)
            {
                if (!string.Equals(pr.TargetRefName, expectedTargetRef, StringComparison.Ordinal))
                {
                    continue;
                }
                if (string.Equals(pr.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    activeMatch = pr;
                    break;
                }
                if (string.Equals(pr.Status, "completed", StringComparison.OrdinalIgnoreCase)
                    && (completedMatch is null || pr.CreationDate < completedMatch.CreationDate))
                {
                    // Prefer the OLDEST completed match — see PrCommands.OpenMergeGroupAdo.cs
                    // for rationale (AB#3228 newer-phantom guard).
                    completedMatch = pr;
                }
            }

            // Branch-recycle staleness check (AB#3211 root cause): if the
            // branch name was reused by a later run, the completed PR's
            // recorded source SHA no longer matches origin/{head}. Drop the
            // stale match so we create a fresh active PR.
            if (activeMatch is null && completedMatch is not null)
            {
                var validity = await ValidateCompletedAdoPrAsync(
                    organization, project, repository,
                    completedMatch.PullRequestId, headBranch, resolvedBase, ct).ConfigureAwait(false);
                if (!validity.IsValid) completedMatch = null;
            }

            var existing = activeMatch ?? completedMatch;

            if (existing is not null)
            {
                // W10 (AB#3291): refuse foreign-lineage adoption.
                var lineageReason = await CheckAdoPrLineageAsync(
                    organization, project, repository, existing.PullRequestId,
                    bodyHasFrontMatter: false,
                    journalAction: "pr_open_evidence_pr",
                    journalTarget: BranchPairJournalTarget(headBranch, resolvedBase),
                    ct).ConfigureAwait(false);
                if (lineageReason is not null)
                {
                    return EvidenceAdoOutcome.Failure(headBranch, resolvedBase,
                        "foreign_lineage", "foreign_lineage: " + lineageReason);
                }

                return new EvidenceAdoOutcome(
                    PrNumber: existing.PullRequestId,
                    PrUrl: BuildAdoPrUrl(organization, project, repository, existing.PullRequestId),
                    Title: prTitle,
                    HeadBranch: headBranch,
                    BaseBranch: resolvedBase,
                    Created: false,
                    ErrorCode: "",
                    Error: null);
            }

            var created = await ado.CreatePullRequestAsync(
                organization, project, repository,
                sourceBranch: headBranch,
                targetBranch: resolvedBase,
                title: prTitle,
                description: prBody,
                ct).ConfigureAwait(false);

            if (created is null)
            {
                return EvidenceAdoOutcome.Failure(headBranch, resolvedBase,
                    "pr_not_found",
                    $"Repository '{repository}' not found in {organization}/{project}.");
            }

            return new EvidenceAdoOutcome(
                PrNumber: created.PullRequestId,
                PrUrl: BuildAdoPrUrl(organization, project, repository, created.PullRequestId),
                Title: prTitle,
                HeadBranch: headBranch,
                BaseBranch: resolvedBase,
                Created: true,
                ErrorCode: "",
                Error: null);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            return EvidenceAdoOutcome.Failure(headBranch, resolvedBase, "no_pat", ex.Message);
        }
        catch (TimeoutException ex)
        {
            return EvidenceAdoOutcome.Failure(headBranch, resolvedBase, "ado_timeout", ex.Message);
        }
        catch (HttpRequestException ex)
        {
            var code = ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "no_pat"
                : "ado_failed";
            return EvidenceAdoOutcome.Failure(headBranch, resolvedBase, code, ex.Message);
        }
        catch (Exception ex)
        {
            return EvidenceAdoOutcome.Failure(headBranch, resolvedBase, "ado_failed", ex.Message);
        }
    }

    /// <summary>
    /// Internal carrier for the platform-neutral fields produced by
    /// <see cref="OpenEvidenceAdoCoreAsync"/>. Both the legacy ADO-only
    /// envelope and the unified evidence-PR envelope are populated from
    /// this struct.
    /// </summary>
    internal readonly record struct EvidenceAdoOutcome(
        int PrNumber,
        string PrUrl,
        string Title,
        string HeadBranch,
        string BaseBranch,
        bool Created,
        string ErrorCode,
        string? Error)
    {
        public static EvidenceAdoOutcome Failure(
            string headBranch, string baseBranch, string errorCode, string message)
            => new(
                PrNumber: 0,
                PrUrl: string.Empty,
                Title: string.Empty,
                HeadBranch: headBranch,
                BaseBranch: baseBranch,
                Created: false,
                ErrorCode: errorCode,
                Error: message);
    }

    private static void EmitOpenEvidenceAdo(PrOpenEvidenceAdoResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrOpenEvidenceAdoResult));

    private static void EmitOpenEvidenceAdoError(
        int workItem,
        int rootId,
        string organization,
        string project,
        string repository,
        string slug,
        string errorCode,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitOpenEvidenceAdo(new PrOpenEvidenceAdoResult
        {
            WorkItemId = workItem,
            RootId = rootId,
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            Organization = organization ?? string.Empty,
            Project = project ?? string.Empty,
            Repository = repository ?? string.Empty,
            RepoSlug = slug ?? string.Empty,
            PrNumber = 0,
            PrUrl = string.Empty,
            Title = string.Empty,
            Created = false,
            ErrorCode = errorCode,
            Error = message,
        });
    }
}
