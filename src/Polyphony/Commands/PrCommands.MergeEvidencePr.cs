using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Squash-merge an evidence PR — the unified, platform-aware verb that
    /// replaces the bare <c>gh pr merge $prNumber --squash --auto --delete-branch</c>
    /// pwsh shell-out previously inlined in <c>actionable.yaml</c>'s
    /// <c>merge_evidence_pr</c> step.
    ///
    /// <para><b>GitHub leg</b>: <c>gh pr merge --squash --auto --delete-branch</c>
    /// queues the merge for after policy/check completion. <see cref="PrMergeEvidenceResult.Merged"/>
    /// reports "queued" — the actual merge SHA is asynchronously resolved.</para>
    ///
    /// <para><b>ADO leg</b>: dispatches to <see cref="MergeEvidenceAdo"/> via
    /// stdout-capture (mirrors the <see cref="MergeImplPr"/> bridge pattern).
    /// ADO has no auto-merge endpoint — the merge is attempted immediately.</para>
    /// </summary>
    /// <param name="prNumber">PR number to merge (already opened by upstream <c>open-evidence-pr</c>).</param>
    /// <param name="prUrl">Optional PR URL echo for the result envelope.</param>
    /// <param name="platform">Platform override (<c>github</c>|<c>ado</c>). Empty for origin-URL auto-detect.</param>
    /// <param name="organization">ADO organization. Required when platform=ado.</param>
    /// <param name="project">ADO project. Required when platform=ado.</param>
    /// <param name="repository">For ADO: repository name/GUID. For GitHub: <c>owner/name</c> slug. Required when <paramref name="platform"/> is non-empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("merge-evidence-pr")]
    [VerbResult(typeof(PrMergeEvidenceResult))]
    public async Task<int> MergeEvidencePr(
        int prNumber = RequiredInput.MissingInt,
        string prUrl = "",
        string platform = "",
        string organization = "",
        string project = "",
        string repository = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr merge-evidence-pr",
            ("--pr-number", prNumber == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (prNumber <= 0)
        {
            EmitMergeEvidenceError(prNumber, prUrl, $"prNumber must be positive (got {prNumber})");
            return ExitCodes.RoutingFailure;
        }

        var resolved = await repoIdentityResolver
            .ResolveAsync(platform, organization, project, repository, ct)
            .ConfigureAwait(false);

        if (resolved.Identity is Polyphony.Sdlc.Observers.RepoIdentity.AdoRepo adoRepo)
        {
            return await DispatchMergeEvidenceAdoAsync(adoRepo, prNumber, prUrl, ct).ConfigureAwait(false);
        }

        if (resolved.Identity is not Polyphony.Sdlc.Observers.RepoIdentity.GitHubRepo ghRepo)
        {
            EmitMergeEvidenceError(prNumber, prUrl,
                $"Could not resolve repo identity from origin remote{(string.IsNullOrEmpty(resolved.Error) ? "" : $": {resolved.Error}")}");
            return ExitCodes.RoutingFailure;
        }

        var slug = $"{ghRepo.Owner}/{ghRepo.Name}";

        try
        {
            var result = await gh.MergePullRequestAsync(
                slug,
                prNumber,
                GhMergeMethod.Squash,
                admin: false,
                deleteBranch: true,
                matchHeadCommit: null,
                auto: true,
                ct: ct).ConfigureAwait(false);

            EmitMergeEvidence(new PrMergeEvidenceResult
            {
                PrNumber = prNumber,
                PrUrl = prUrl,
                Merged = result.Succeeded,
                AlreadyMerged = result.AlreadyMerged,
                MergeCommit = result.MergeSha ?? "",
                Repository = slug,
                RepoSlug = slug,
            });
            return result.Succeeded ? ExitCodes.Success : ExitCodes.RoutingFailure;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitMergeEvidenceError(prNumber, prUrl, ex.Message, slug);
            return ExitCodes.RoutingFailure;
        }
    }

    /// <summary>
    /// Bridge from <see cref="MergeEvidencePr"/>'s ADO branch into the legacy
    /// <see cref="MergeEvidenceAdo"/> verb body. Captures the verb's stdout
    /// JSON (<see cref="PrMergeEvidenceAdoResult"/>), parses it, and re-emits
    /// a <see cref="PrMergeEvidenceResult"/> populated with both the
    /// platform-neutral fields and the ADO echo fields.
    /// </summary>
    private async Task<int> DispatchMergeEvidenceAdoAsync(
        Polyphony.Sdlc.Observers.RepoIdentity.AdoRepo adoRepo,
        int prNumber,
        string prUrl,
        CancellationToken ct)
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);
        int exitCode;
        try
        {
            exitCode = await MergeEvidenceAdo(
                adoRepo.Organization, adoRepo.Project, adoRepo.Repository,
                prNumber, ct).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(origOut);
        }

        var json = sw.ToString();
        PrMergeEvidenceAdoResult? adoResult = null;
        try
        {
            adoResult = JsonSerializer.Deserialize(
                json.Trim(),
                PolyphonyJsonContext.Default.PrMergeEvidenceAdoResult);
        }
        catch (JsonException) { /* malformed — fall through */ }

        if (adoResult is null)
        {
            EmitMergeEvidence(new PrMergeEvidenceResult
            {
                PrNumber = prNumber,
                PrUrl = prUrl,
                Merged = false,
                AlreadyMerged = false,
                MergeCommit = "",
                Organization = adoRepo.Organization,
                Project = adoRepo.Project,
                Repository = adoRepo.Repository,
                RepoSlug = BuildAdoSlug(adoRepo.Organization, adoRepo.Project, adoRepo.Repository),
                Error = $"merge-evidence-ado bridge: failed to parse output (exit {exitCode}): {json.Trim()}",
            });
            return ExitCodes.RoutingFailure;
        }

        EmitMergeEvidence(new PrMergeEvidenceResult
        {
            PrNumber = adoResult.PrNumber,
            PrUrl = !string.IsNullOrEmpty(adoResult.PrUrl) ? adoResult.PrUrl : prUrl,
            Merged = adoResult.Merged,
            AlreadyMerged = adoResult.AlreadyMerged,
            MergeCommit = adoResult.MergeCommit,
            Organization = adoResult.Organization,
            Project = adoResult.Project,
            Repository = adoResult.Repository,
            RepoSlug = adoResult.RepoSlug,
            Error = string.IsNullOrEmpty(adoResult.Error) ? null : adoResult.Error,
        });
        return string.IsNullOrEmpty(adoResult.ErrorCode) ? ExitCodes.Success : ExitCodes.RoutingFailure;
    }

    private static void EmitMergeEvidence(PrMergeEvidenceResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrMergeEvidenceResult));

    private static void EmitMergeEvidenceError(
        int prNumber,
        string prUrl,
        string message,
        string repoSlug = "")
    {
        EmitMergeEvidence(new PrMergeEvidenceResult
        {
            PrNumber = prNumber,
            PrUrl = prUrl,
            Merged = false,
            AlreadyMerged = false,
            MergeCommit = "",
            Repository = repoSlug,
            RepoSlug = repoSlug,
            Error = message,
        });
    }
}
