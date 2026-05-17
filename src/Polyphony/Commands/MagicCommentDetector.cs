using System.Text.RegularExpressions;

namespace Polyphony.Commands;

/// <summary>
/// Soft-deprecation detector for the retired <c>polyphony:approve</c>
/// and <c>polyphony:request-changes</c> magic-comment workarounds.
/// After the sentiment-driven PR review redesign, these comments no
/// longer influence routing (the platform UI is now authoritative).
/// Detecting them lets us emit a single warning into the verb's
/// <c>warnings[]</c> envelope so operators who have muscle-memory for
/// posting them realize they are silent no-ops.
///
/// <para>Scope: detect-only. Does NOT synthesize reviewer entries,
/// does NOT influence the <c>state</c> field derivation, does NOT
/// affect any routing decision. One-release grace period before the
/// detector itself is removed.</para>
/// </summary>
internal static class MagicCommentDetector
{
    private static readonly Regex MagicApprove = new(
        @"^\s*polyphony:approve(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MagicRequestChanges = new(
        @"^\s*polyphony:request-changes(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The single deprecation warning text emitted to <c>warnings[]</c>
    /// when one or more magic comments were detected in the poll. The
    /// count is interpolated so operators can see whether it is a
    /// stale single comment or an ongoing pattern.
    /// </summary>
    public const string WarningPrefix =
        "Magic comments (polyphony:approve / polyphony:request-changes) are no longer honored " +
        "for routing — vote, merge, or comment via the platform UI instead. ";

    /// <summary>
    /// Format the warning with a detected-count suffix.
    /// </summary>
    public static string FormatWarning(int count) =>
        count == 1
            ? WarningPrefix + "(Detected 1 stale magic comment.)"
            : WarningPrefix + $"(Detected {count} stale magic comments.)";

    /// <summary>
    /// Count magic comments across the supplied bodies. Each body is
    /// scanned for both <c>polyphony:approve</c> and
    /// <c>polyphony:request-changes</c>; multiple matches in a single
    /// body count multiply.
    /// </summary>
    public static int Count(IEnumerable<string?> bodies)
    {
        var n = 0;
        foreach (var body in bodies)
        {
            if (string.IsNullOrEmpty(body)) continue;
            n += MagicApprove.Matches(body).Count;
            n += MagicRequestChanges.Matches(body).Count;
        }
        return n;
    }
}
