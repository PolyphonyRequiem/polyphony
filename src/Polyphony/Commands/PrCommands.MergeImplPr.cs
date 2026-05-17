using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class PrCommands
{
    /// <summary>
    /// Merge the per-item impl PR into its enclosing merge-group branch.
    /// Identifies the PR by its (head, base) pair: head is
    /// <c>impl/{root_id}-{item_id}</c>, base is <c>mg/{root_id}_{mg_path}</c>.
    /// Default merge method is squash because impl PRs carry micro-history
    /// we do not want to pollute the merge-group branch with; the planner
    /// may override per item via <paramref name="method"/>.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's apex (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the task.</param>
    /// <param name="mgPath">Canonical <c>_</c>-joined merge-group path of the enclosing MG.</param>
    /// <param name="method">
    /// Merge method: <c>squash</c> (default), <c>merge</c>, or <c>rebase</c>.
    /// </param>
    /// <param name="admin">Pass <c>--admin</c> to bypass branch-protection requirements.</param>
    /// <param name="deleteBranch">
    /// Delete the head branch after merge. Default <c>"true"</c>. Accepts
    /// <c>"true"</c> or <c>"false"</c> (case-insensitive) — declared as
    /// <see cref="string"/> rather than <see cref="bool"/> so workflow YAMLs
    /// can pass the explicit-value form (e.g.
    /// <c>--delete-branch false</c>); ConsoleAppFramework treats <c>bool</c>
    /// params as no-value-only switches and rejects the value form.
    /// Parsed via <see cref="StringBoolArg.Parse"/>; non-recognised values
    /// halt with a routing-style error envelope.
    /// </param>
    /// <param name="matchHeadCommit">
    /// When set, gh refuses to merge if the head SHA has moved off this
    /// commit. Use to guard against races between status checks and merge.
    /// </param>
    /// <param name="platform">Platform override (<c>github</c>|<c>ado</c>). Empty for origin-URL auto-detect.</param>
    /// <param name="organization">ADO organization. Required when platform=ado.</param>
    /// <param name="project">ADO project. Required when platform=ado.</param>
    /// <param name="repository">For ADO: repository name/GUID. For GitHub: <c>owner/name</c> slug. Required when <paramref name="platform"/> is non-empty.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("merge-impl-pr")]
    [VerbResult(typeof(PrMergeImplResult))]
    public async Task<int> MergeImplPr(
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        string mgPath = "",
        string method = "squash",
        bool admin = false,
        string deleteBranch = "true",
        string matchHeadCommit = "",
        string platform = "",
        string organization = "",
        string project = "",
        string repository = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("pr merge-impl-pr",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt),
            ("--mg-path", string.IsNullOrEmpty(mgPath))) is { } halt)
            return halt;

        var deleteBranchParsed = StringBoolArg.Parse("pr merge-impl-pr", "--delete-branch", deleteBranch);
        if (deleteBranchParsed is null) return ExitCodes.RoutingFailure;
        var deleteBranchBool = deleteBranchParsed.Value;

        // ── Platform-aware identity resolution ───────────────────────────
        // Resolve early so the ADO branch can dispatch via the existing
        // MergeImplAdo verb (output captured + remapped to the unified
        // envelope). Bridge approach (rather than helper extraction) keeps
        // the ~280-line ADO body untouched at this stage; the *Pr verb
        // remains the single entry point for workflows.
        var resolved = await repoIdentityResolver
            .ResolveAsync(platform, organization, project, repository, ct)
            .ConfigureAwait(false);

        if (resolved.Identity is Polyphony.Sdlc.Observers.RepoIdentity.AdoRepo adoRepo)
        {
            // ADO impl merges are squash-only with branch deletion (per
            // MergeImplAdo's hardcoded ImplMethod / ImplDeleteBranch). Honor
            // the *Pr verb's defaults but warn on incompatible overrides.
            return await DispatchMergeImplAdoAsync(
                adoRepo, rootId, itemId, mgPath, method, deleteBranchBool, matchHeadCommit, ct)
                .ConfigureAwait(false);
        }

        if (!Branching.RootId.TryParse(rootId, out var root))
        {
            EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranchBool, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }

        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranchBool, $"itemId must be positive (got {itemId})");
            return ExitCodes.ConfigError;
        }

        if (!MergeGroupPath.TryParse(mgPath, out var path) || path is null)
        {
            EmitMergeImplError(
                rootId,
                itemId,
                mgPath,
                method,
                deleteBranchBool,
                $"'{mgPath}' is not a valid merge-group path. Each segment must match {MergeGroupId.GrammarPattern}; segments are joined by '_'.");
            return ExitCodes.ConfigError;
        }

        if (!TryParseMethod(method, out var mergeMethod, out var methodError))
        {
            EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranchBool, methodError);
            return ExitCodes.ConfigError;
        }

        var headBranch = BranchNameBuilder.Impl(root, item).Value;
        var baseBranch = BranchNameBuilder.MergeGroup(root, path).Value;

        try
        {
            var slug = await TryResolveSlugAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(slug))
            {
                EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranchBool, "Could not resolve repo slug from origin remote", headBranch, baseBranch);
                return ExitCodes.RoutingFailure;
            }

            var resolution = await FindPrForMergeAsync(slug, headBranch, baseBranch, ct).ConfigureAwait(false);
            if (resolution.Error is not null)
            {
                EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranchBool, resolution.Error, headBranch, baseBranch);
                return ExitCodes.RoutingFailure;
            }

            // Idempotent: if the matching PR is already merged, treat as success.
            if (resolution.AlreadyMergedPr is { } already)
            {
                EmitMergeImpl(new PrMergeImplResult
                {
                    PrNumber = already.Number,
                    HeadBranch = headBranch,
                    BaseBranch = baseBranch,
                    RootId = rootId,
                    ItemId = itemId,
                    MgPath = path.Canonical,
                    Method = method,
                    Merged = true,
                    AlreadyMerged = true,
                    DeleteBranch = deleteBranchBool,
                    MergeSha = null,
                });
                return ExitCodes.Success;
            }

            var openPr = resolution.OpenPr!;
            var mergeMatch = string.IsNullOrEmpty(matchHeadCommit) ? null : matchHeadCommit;
            var result = await gh.MergePullRequestAsync(
                slug, openPr.Number, mergeMethod, admin, deleteBranchBool, mergeMatch, ct: ct).ConfigureAwait(false);

            EmitMergeImpl(new PrMergeImplResult
            {
                PrNumber = openPr.Number,
                HeadBranch = headBranch,
                BaseBranch = baseBranch,
                RootId = rootId,
                ItemId = itemId,
                MgPath = path.Canonical,
                Method = method,
                Merged = result.Succeeded,
                AlreadyMerged = result.AlreadyMerged,
                DeleteBranch = deleteBranchBool,
                MergeSha = result.MergeSha,
            });
            return result.Succeeded ? ExitCodes.Success : ExitCodes.RoutingFailure;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitMergeImplError(rootId, itemId, mgPath, method, deleteBranchBool, ex.Message, headBranch, baseBranch);
            return ExitCodes.RoutingFailure;
        }
    }

    private static void EmitMergeImpl(PrMergeImplResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.PrMergeImplResult));

    /// <summary>
    /// Bridge from <see cref="MergeImplPr"/>'s ADO branch into the legacy
    /// <see cref="MergeImplAdo"/> verb body. Captures the verb's stdout JSON
    /// (the <see cref="PrMergeImplAdoResult"/> envelope), parses it, and
    /// re-emits a <see cref="PrMergeImplResult"/> populated with both the
    /// platform-neutral fields and the ADO-specific echo fields. Avoids
    /// extracting a large helper from MergeImplAdo's ~280-line body until a
    /// future refactor pass.
    /// </summary>
    private async Task<int> DispatchMergeImplAdoAsync(
        Polyphony.Sdlc.Observers.RepoIdentity.AdoRepo adoRepo,
        int rootId,
        int itemId,
        string mgPath,
        string method,
        bool deleteBranch,
        string matchHeadCommit,
        CancellationToken ct)
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);
        int exitCode;
        try
        {
            exitCode = await MergeImplAdo(
                adoRepo.Organization, adoRepo.Project, adoRepo.Repository,
                rootId, itemId, mgPath, matchHeadCommit,
                deleteBranch: deleteBranch ? "true" : "false",
                ct: ct).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(origOut);
        }

        var json = sw.ToString();
        PrMergeImplAdoResult? adoResult = null;
        try
        {
            adoResult = JsonSerializer.Deserialize(
                json.Trim(),
                PolyphonyJsonContext.Default.PrMergeImplAdoResult);
        }
        catch (JsonException) { /* malformed — fall through to error envelope */ }

        if (adoResult is null)
        {
            EmitMergeImpl(new PrMergeImplResult
            {
                PrNumber = 0,
                HeadBranch = "",
                BaseBranch = "",
                RootId = rootId,
                ItemId = itemId,
                MgPath = mgPath,
                Method = "squash",
                Merged = false,
                AlreadyMerged = false,
                DeleteBranch = deleteBranch,
                MergeSha = null,
                Organization = adoRepo.Organization,
                Project = adoRepo.Project,
                Repository = adoRepo.Repository,
                RepoSlug = BuildAdoSlug(adoRepo.Organization, adoRepo.Project, adoRepo.Repository),
                Error = $"merge-impl-ado bridge: failed to parse output (exit {exitCode}): {json.Trim()}",
            });
            return ExitCodes.RoutingFailure;
        }

        EmitMergeImpl(new PrMergeImplResult
        {
            PrNumber = adoResult.PrNumber,
            HeadBranch = adoResult.HeadBranch,
            BaseBranch = adoResult.BaseBranch,
            RootId = adoResult.RootId,
            ItemId = adoResult.ItemId,
            MgPath = adoResult.MgPath,
            Method = adoResult.Method,
            Merged = adoResult.Merged,
            AlreadyMerged = adoResult.AlreadyMerged,
            DeleteBranch = adoResult.DeleteBranch,
            MergeSha = string.IsNullOrEmpty(adoResult.MergeCommit) ? null : adoResult.MergeCommit,
            Organization = adoResult.Organization,
            Project = adoResult.Project,
            Repository = adoResult.Repository,
            RepoSlug = adoResult.RepoSlug,
            Error = string.IsNullOrEmpty(adoResult.Error) ? null : adoResult.Error,
        });
        return string.IsNullOrEmpty(adoResult.Error) ? ExitCodes.Success : ExitCodes.RoutingFailure;
    }

    private static void EmitMergeImplError(
        int rootId,
        int itemId,
        string mgPath,
        string method,
        bool deleteBranch,
        string message,
        string headBranch = "",
        string baseBranch = "")
    {
        EmitMergeImpl(new PrMergeImplResult
        {
            PrNumber = 0,
            HeadBranch = headBranch,
            BaseBranch = baseBranch,
            RootId = rootId,
            ItemId = itemId,
            MgPath = mgPath,
            Method = method,
            Merged = false,
            AlreadyMerged = false,
            DeleteBranch = deleteBranch,
            MergeSha = null,
            Error = message,
        });
    }
}
