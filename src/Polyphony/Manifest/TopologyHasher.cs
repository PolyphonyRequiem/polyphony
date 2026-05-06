using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Polyphony.Manifest;

/// <summary>
/// Computes the topology hash defined in the Rev 4 branch-model ADR
/// (<c>docs/decisions/branch-model.md</c> § Topology hash inputs).
///
/// <para>The hash is a SHA-256 over a canonicalized text representation
/// — NOT the YAML text. Canonicalization rules:</para>
/// <list type="number">
///   <item><description>Each MG contributes one record:
///   <c>(mg_path, items_sorted_asc, isolation, nesting_override_or_null)</c>.</description></item>
///   <item><description>Records are sorted by <c>mg_path</c> (lexicographic, ordinal).</description></item>
///   <item><description>Within each record, items are sorted ascending.</description></item>
///   <item><description>Each record is one tab-separated UTF-8 line:
///   <c>mg_path\titems_csv\tisolation\tnesting_override\n</c>
///   where <c>nesting_override</c> is the literal string <c>"null"</c> when absent.</description></item>
///   <item><description>The full canonical text is the concatenation of all lines.</description></item>
///   <item><description>The hash is <c>sha256:{hex}</c>.</description></item>
/// </list>
///
/// <para>Excluded from the hash (per the ADR): <c>plan_generations</c>,
/// <c>rebases</c>, <c>human_approvals</c>, <c>retired_merge_group_ids</c>.</para>
///
/// <para>The empty MG set produces the SHA-256 of empty UTF-8 text:
/// <c>sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855</c>.</para>
/// </summary>
public static class TopologyHasher
{
    private const string Prefix = "sha256:";

    /// <summary>
    /// Computes the topology hash for the given merge-group entries.
    /// Pure function — no I/O.
    /// </summary>
    public static string ComputeHash(IEnumerable<MergeGroupEntry> mergeGroups)
    {
        ArgumentNullException.ThrowIfNull(mergeGroups);

        var canonicalText = BuildCanonicalText(mergeGroups);
        var bytes = Encoding.UTF8.GetBytes(canonicalText);
        var digest = SHA256.HashData(bytes);
        return Prefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Builds the canonical text used as the hash input. Exposed for
    /// debug/test introspection — the contract under test is "this
    /// matches the ADR canonicalization rules byte-for-byte".
    /// </summary>
    public static string BuildCanonicalText(IEnumerable<MergeGroupEntry> mergeGroups)
    {
        ArgumentNullException.ThrowIfNull(mergeGroups);

        // Materialize and sort by mg_path (ordinal) per rule 2.
        var ordered = mergeGroups
            .Select(BuildRecordTuple)
            .OrderBy(t => t.MgPath, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        foreach (var record in ordered)
        {
            builder.Append(record.MgPath);
            builder.Append('\t');
            builder.Append(record.ItemsCsv);
            builder.Append('\t');
            builder.Append(record.Isolation);
            builder.Append('\t');
            builder.Append(record.NestingOverride);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static (string MgPath, string ItemsCsv, string Isolation, string NestingOverride)
        BuildRecordTuple(MergeGroupEntry entry)
    {
        // Sort items ascending (rule 3) and join with comma (rule 4).
        var sortedItems = entry.Items
            .OrderBy(i => i)
            .Select(i => i.ToString(CultureInfo.InvariantCulture));
        var itemsCsv = string.Join(",", sortedItems);

        // null override canonicalizes to the literal "null" (rule 4).
        var overrideText = entry.NestingOverride ?? "null";

        return (entry.MgPath, itemsCsv, entry.Isolation, overrideText);
    }
}
