using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Polyphony.Infrastructure.Worktrees;

/// <summary>
/// Parses a polyphony branch name against the canonical grammar from
/// <c>.github/skills/polyphony-branch-model/SKILL.md</c> and emits both a
/// structured <see cref="ParsedBranch"/> and a filesystem-safe slug.
///
/// <para>Grammar (Rev 4):</para>
/// <list type="bullet">
///   <item><c>feature/{root}</c> — root is a positive int (work item id)</item>
///   <item><c>plan/{root}</c> — root plan</item>
///   <item><c>plan/{root}-{item}</c> — descendant plan; flat (leaf id only)</item>
///   <item><c>mg/{root}_{mg_path}</c> — <c>mg_path</c> is one or more MG ids joined by <c>_</c>; each MG id matches <c>^[a-z][a-z0-9-]{0,30}$</c></item>
///   <item><c>impl/{root}-{item}</c> — flat impl branch</item>
///   <item><c>evidence/{root}-{item}</c> — flat evidence branch</item>
/// </list>
///
/// <para>The slug is the branch with <c>/</c> replaced by <c>-</c>:
/// <c>impl/3085-3072</c> → <c>impl-3085-3072</c>;
/// <c>mg/3085_pg-foo</c> → <c>mg-3085_pg-foo</c>. Slugs are used as
/// directory names under <c>polyphony-runs/apex-{root}/</c>.</para>
///
/// <para>Used by <c>polyphony worktree create</c> (AB#3085, PR 1b3) to
/// validate that <c>--branch</c> matches the grammar AND that
/// <see cref="ParsedBranch.RootId"/> matches <c>--apex</c>, catching cross-apex
/// invocations (<c>worktree create --apex 3085 --branch impl/9999-1234</c>)
/// before any git operation.</para>
/// </summary>
public static class BranchSlug
{
    /// <summary>
    /// Per-segment MG id grammar (Rev 4): lower-case alphanumerics and
    /// hyphens, must start with a letter, max 31 chars. Matches a SINGLE
    /// MG id segment — multiple segments are joined by <c>_</c> in
    /// <c>mg_path</c>.
    /// </summary>
    private static readonly Regex MgIdSegment = new(
        "^[a-z][a-z0-9-]{0,30}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Try to parse <paramref name="branch"/> against the canonical grammar.
    /// </summary>
    /// <param name="branch">Branch name to parse (e.g. <c>impl/3085-3072</c>).</param>
    /// <param name="parsed">On success, the structured parse result.</param>
    /// <param name="rejection">On failure, a one-line operator-readable rejection reason.</param>
    /// <returns><c>true</c> when the branch matches the grammar; <c>false</c> otherwise.</returns>
    public static bool TryParse(
        string branch,
        [NotNullWhen(true)] out ParsedBranch? parsed,
        [NotNullWhen(false)] out string? rejection)
    {
        parsed = null;
        rejection = null;

        if (string.IsNullOrEmpty(branch))
        {
            rejection = "branch is empty";
            return false;
        }

        // Reject anything that would let an operator inject path traversal
        // via the slug (e.g. `feature/../../etc`).
        if (branch.Contains("..", StringComparison.Ordinal)
            || branch.Contains('\\', StringComparison.Ordinal)
            || branch.Contains('\0', StringComparison.Ordinal))
        {
            rejection = $"branch contains forbidden characters: '{branch}'";
            return false;
        }

        var slashIndex = branch.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex <= 0 || slashIndex == branch.Length - 1)
        {
            rejection = $"branch must contain a non-empty prefix and payload separated by '/': '{branch}'";
            return false;
        }
        if (branch.IndexOf('/', slashIndex + 1) >= 0)
        {
            // Multiple slashes are not permitted in the canonical grammar:
            // `/` is reserved for the ref-class prefix only.
            rejection = $"branch has multiple '/' separators (only one allowed): '{branch}'";
            return false;
        }

        var prefix = branch[..slashIndex];
        var payload = branch[(slashIndex + 1)..];

        switch (prefix)
        {
            case "feature":
                return TryParseFeature(branch, payload, out parsed, out rejection);
            case "plan":
                return TryParsePlan(branch, payload, out parsed, out rejection);
            case "impl":
                return TryParseImpl(branch, payload, out parsed, out rejection);
            case "evidence":
                return TryParseEvidence(branch, payload, out parsed, out rejection);
            case "mg":
                return TryParseMg(branch, payload, out parsed, out rejection);
            default:
                rejection = $"unknown branch prefix '{prefix}/' (expected feature|plan|mg|impl|evidence)";
                return false;
        }
    }

    private static bool TryParseFeature(
        string branch,
        string payload,
        [NotNullWhen(true)] out ParsedBranch? parsed,
        [NotNullWhen(false)] out string? rejection)
    {
        if (!TryParsePositiveInt(payload, out var root))
        {
            parsed = null;
            rejection = $"feature branch payload must be a positive int (root id): '{branch}'";
            return false;
        }
        parsed = new ParsedBranch(BranchKind.Feature, root, Slug(branch), MgSegments: null, ItemId: null);
        rejection = null;
        return true;
    }

    private static bool TryParsePlan(
        string branch,
        string payload,
        [NotNullWhen(true)] out ParsedBranch? parsed,
        [NotNullWhen(false)] out string? rejection)
    {
        // Either `{root}` (root plan) or `{root}-{item}` (descendant plan).
        var dashIndex = payload.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex < 0)
        {
            if (!TryParsePositiveInt(payload, out var rootOnly))
            {
                parsed = null;
                rejection = $"plan branch payload must be '{{root}}' or '{{root}}-{{item}}': '{branch}'";
                return false;
            }
            parsed = new ParsedBranch(BranchKind.Plan, rootOnly, Slug(branch), MgSegments: null, ItemId: null);
            rejection = null;
            return true;
        }

        var rootStr = payload[..dashIndex];
        var itemStr = payload[(dashIndex + 1)..];
        if (!TryParsePositiveInt(rootStr, out var root)
            || !TryParsePositiveInt(itemStr, out var item))
        {
            parsed = null;
            rejection = $"plan descendant branch must be 'plan/{{root}}-{{item}}' with positive ints: '{branch}'";
            return false;
        }
        parsed = new ParsedBranch(BranchKind.Plan, root, Slug(branch), MgSegments: null, ItemId: item);
        rejection = null;
        return true;
    }

    private static bool TryParseImpl(
        string branch,
        string payload,
        [NotNullWhen(true)] out ParsedBranch? parsed,
        [NotNullWhen(false)] out string? rejection)
        => TryParseFlatRootItem(branch, payload, BranchKind.Impl, "impl", out parsed, out rejection);

    private static bool TryParseEvidence(
        string branch,
        string payload,
        [NotNullWhen(true)] out ParsedBranch? parsed,
        [NotNullWhen(false)] out string? rejection)
        => TryParseFlatRootItem(branch, payload, BranchKind.Evidence, "evidence", out parsed, out rejection);

    private static bool TryParseFlatRootItem(
        string branch,
        string payload,
        BranchKind kind,
        string kindName,
        [NotNullWhen(true)] out ParsedBranch? parsed,
        [NotNullWhen(false)] out string? rejection)
    {
        var dashIndex = payload.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex <= 0 || dashIndex == payload.Length - 1)
        {
            parsed = null;
            rejection = $"{kindName} branch must be '{kindName}/{{root}}-{{item}}': '{branch}'";
            return false;
        }
        var rootStr = payload[..dashIndex];
        var itemStr = payload[(dashIndex + 1)..];
        if (!TryParsePositiveInt(rootStr, out var root)
            || !TryParsePositiveInt(itemStr, out var item))
        {
            parsed = null;
            rejection = $"{kindName} branch must be '{kindName}/{{root}}-{{item}}' with positive ints: '{branch}'";
            return false;
        }
        parsed = new ParsedBranch(kind, root, Slug(branch), MgSegments: null, ItemId: item);
        rejection = null;
        return true;
    }

    private static bool TryParseMg(
        string branch,
        string payload,
        [NotNullWhen(true)] out ParsedBranch? parsed,
        [NotNullWhen(false)] out string? rejection)
    {
        // Payload: `{root}_{mg_id}[_{mg_id}...]`. The root id is separated
        // from the first MG segment by `_` (NOT `-`); descendant MG segments
        // are joined by further `_`.
        var firstUnderscore = payload.IndexOf('_', StringComparison.Ordinal);
        if (firstUnderscore <= 0 || firstUnderscore == payload.Length - 1)
        {
            parsed = null;
            rejection = $"mg branch must be 'mg/{{root}}_{{mg_id}}[_{{mg_id}}...]': '{branch}'";
            return false;
        }
        var rootStr = payload[..firstUnderscore];
        var mgPath = payload[(firstUnderscore + 1)..];
        if (!TryParsePositiveInt(rootStr, out var root))
        {
            parsed = null;
            rejection = $"mg branch root must be a positive int: '{branch}'";
            return false;
        }
        var segments = mgPath.Split('_', StringSplitOptions.None);
        foreach (var segment in segments)
        {
            if (!MgIdSegment.IsMatch(segment))
            {
                parsed = null;
                rejection = $"mg id segment '{segment}' violates ^[a-z][a-z0-9-]{{0,30}}$ in '{branch}'";
                return false;
            }
        }
        parsed = new ParsedBranch(BranchKind.Mg, root, Slug(branch), MgSegments: segments, ItemId: null);
        rejection = null;
        return true;
    }

    /// <summary>
    /// The slug used as a directory name under
    /// <c>polyphony-runs/apex-{root}/</c>. Replaces the single ref-class
    /// <c>/</c> with <c>-</c>; underscores in <c>mg_path</c> are preserved.
    /// </summary>
    private static string Slug(string branch) => branch.Replace('/', '-');

    private static bool TryParsePositiveInt(string s, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s))
            return false;
        // Reject leading zeros / signs / whitespace — git refs use bare
        // decimal ids and any deviation should be loudly rejected.
        foreach (var c in s)
        {
            if (c < '0' || c > '9')
                return false;
        }
        if (s.Length > 1 && s[0] == '0')
            return false;
        return int.TryParse(s, out value) && value > 0;
    }
}

/// <summary>
/// Structural parse of a polyphony branch name. See <see cref="BranchSlug.TryParse"/>.
/// </summary>
/// <param name="Kind">Branch class.</param>
/// <param name="RootId">The <c>{root}</c> work item id (apex id).</param>
/// <param name="Slug">Filesystem-safe directory name (<c>/</c> → <c>-</c>).</param>
/// <param name="MgSegments">Parsed MG hierarchy segments (mg branches only).</param>
/// <param name="ItemId">The <c>{item_id}</c> for plan-descendant / impl / evidence branches.</param>
public sealed record ParsedBranch(
    BranchKind Kind,
    int RootId,
    string Slug,
    IReadOnlyList<string>? MgSegments,
    int? ItemId);

public enum BranchKind
{
    Feature,
    Plan,
    Mg,
    Impl,
    Evidence,
}
