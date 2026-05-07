using System.Text.RegularExpressions;

namespace Polyphony.Branching;

/// <summary>
/// Pure parser for git refs into the <see cref="ParsedBranch"/> DU. The
/// parser is the inverse of <see cref="BranchNameBuilder"/>: a refname
/// produced by the builder always round-trips through
/// <see cref="ParseOrUnrecognized(string)"/> back into the same case with
/// the same components.
/// </summary>
/// <remarks>
/// Refs that fall outside the Rev 4 Polyphony grammar (e.g. <c>main</c>,
/// <c>release/v1</c>, dev branches, malformed Polyphony refs) parse as
/// <see cref="ParsedBranch.Unrecognized"/>; this is deliberate so that
/// consumers can route on the DU rather than catching parse exceptions.
/// </remarks>
internal static partial class BranchNameParser
{
    // Anchored grammar regexes. Numeric ids reject leading zeros so the
    // builder's canonical form is the only accepted representation
    // (i.e. branch identity matches branch text identity).
    private const string PositiveIntPattern = "[1-9][0-9]*";

    [GeneratedRegex($"^feature/(?<root>{PositiveIntPattern})$", RegexOptions.CultureInvariant)]
    private static partial Regex FeatureRegex();

    [GeneratedRegex($"^plan/(?<root>{PositiveIntPattern})$", RegexOptions.CultureInvariant)]
    private static partial Regex RootPlanRegex();

    [GeneratedRegex($"^plan/(?<root>{PositiveIntPattern})-(?<item>{PositiveIntPattern})$", RegexOptions.CultureInvariant)]
    private static partial Regex DescendantPlanRegex();

    [GeneratedRegex($"^impl/(?<root>{PositiveIntPattern})-(?<item>{PositiveIntPattern})$", RegexOptions.CultureInvariant)]
    private static partial Regex ImplRegex();

    [GeneratedRegex($"^evidence/(?<root>{PositiveIntPattern})-(?<item>{PositiveIntPattern})$", RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceRegex();

    [GeneratedRegex($"^evidence/(?<item>{PositiveIntPattern})$", RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceOrphanRegex();

    [GeneratedRegex($"^mg/(?<root>{PositiveIntPattern})_(?<path>[a-z][a-z0-9-]*(?:_[a-z][a-z0-9-]*)*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MergeGroupRegex();

    /// <summary>
    /// Parses any string into a <see cref="ParsedBranch"/>; refs outside
    /// the grammar surface as <see cref="ParsedBranch.Unrecognized"/>.
    /// </summary>
    public static ParsedBranch ParseOrUnrecognized(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (TryParse(raw, out var parsed) && parsed is not null)
        {
            return parsed;
        }

        return new ParsedBranch.Unrecognized(raw);
    }

    /// <summary>
    /// Tries to recognize a string as a Polyphony Rev 4 branch ref. Returns
    /// <c>false</c> for refs outside the grammar; in that case
    /// <paramref name="parsed"/> is <c>null</c> and callers can treat the
    /// ref as <see cref="ParsedBranch.Unrecognized"/> if desired.
    /// </summary>
    public static bool TryParse(string? raw, out ParsedBranch? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        // Order matters only for disambiguation; the patterns are
        // mutually exclusive at the prefix level (feature/, plan/, mg/,
        // impl/, evidence/) so any order is correct. Listed in the
        // ADR's display order for readability.

        var featureMatch = FeatureRegex().Match(raw);
        if (featureMatch.Success)
        {
            var rootId = ParsePositiveInt(featureMatch.Groups["root"].Value);
            parsed = new ParsedBranch.Feature(BranchName.CreateUnsafe(raw), rootId);
            return true;
        }

        var rootPlanMatch = RootPlanRegex().Match(raw);
        if (rootPlanMatch.Success)
        {
            var rootId = ParsePositiveInt(rootPlanMatch.Groups["root"].Value);
            parsed = new ParsedBranch.RootPlan(BranchName.CreateUnsafe(raw), rootId);
            return true;
        }

        var descendantPlanMatch = DescendantPlanRegex().Match(raw);
        if (descendantPlanMatch.Success)
        {
            var rootId = ParsePositiveInt(descendantPlanMatch.Groups["root"].Value);
            var itemId = ParsePositiveItem(descendantPlanMatch.Groups["item"].Value);
            parsed = new ParsedBranch.DescendantPlan(BranchName.CreateUnsafe(raw), rootId, itemId);
            return true;
        }

        var mergeGroupMatch = MergeGroupRegex().Match(raw);
        if (mergeGroupMatch.Success)
        {
            var rootId = ParsePositiveInt(mergeGroupMatch.Groups["root"].Value);
            // The regex already constrained each segment to the grammar,
            // so MergeGroupPath.Parse cannot throw here.
            var path = MergeGroupPath.Parse(mergeGroupMatch.Groups["path"].Value);
            parsed = new ParsedBranch.MergeGroup(BranchName.CreateUnsafe(raw), rootId, path);
            return true;
        }

        var implMatch = ImplRegex().Match(raw);
        if (implMatch.Success)
        {
            var rootId = ParsePositiveInt(implMatch.Groups["root"].Value);
            var itemId = ParsePositiveItem(implMatch.Groups["item"].Value);
            parsed = new ParsedBranch.Impl(BranchName.CreateUnsafe(raw), rootId, itemId);
            return true;
        }

        var evidenceMatch = EvidenceRegex().Match(raw);
        if (evidenceMatch.Success)
        {
            var rootId = ParsePositiveInt(evidenceMatch.Groups["root"].Value);
            var itemId = ParsePositiveItem(evidenceMatch.Groups["item"].Value);
            parsed = new ParsedBranch.Evidence(BranchName.CreateUnsafe(raw), rootId, itemId);
            return true;
        }

        // Orphan evidence (`evidence/{item}`) is checked AFTER the combined
        // form so the longer pattern wins on inputs like `evidence/1-2`.
        var evidenceOrphanMatch = EvidenceOrphanRegex().Match(raw);
        if (evidenceOrphanMatch.Success)
        {
            var itemId = ParsePositiveItem(evidenceOrphanMatch.Groups["item"].Value);
            parsed = new ParsedBranch.EvidenceOrphan(BranchName.CreateUnsafe(raw), itemId);
            return true;
        }

        return false;
    }

    private static RootId ParsePositiveInt(string text)
    {
        // The capture group is anchored to the positive-int pattern, so
        // this cannot fail under normal program flow. We still validate
        // through RootId.Parse because (a) defense in depth, and (b) it
        // documents the invariant at the call site.
        var value = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return RootId.Parse(value);
    }

    private static WorkItemId ParsePositiveItem(string text)
    {
        var value = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return WorkItemId.Parse(value);
    }
}
