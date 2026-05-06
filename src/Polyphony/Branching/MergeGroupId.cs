using System.Text.RegularExpressions;

namespace Polyphony.Branching;

/// <summary>
/// A single segment of a merge-group hierarchy. Validated against the
/// grammar locked in by the Rev 4 branch-model ADR
/// (<c>docs/decisions/branch-model.md</c>): lowercase letter, then
/// up to 30 lowercase alphanumerics or hyphens. The grammar deliberately
/// excludes <c>_</c> so that <see cref="MergeGroupPath"/> can safely use
/// <c>_</c> as the hierarchy delimiter inside <c>mg/</c> branch names.
/// </summary>
internal readonly partial record struct MergeGroupId
{
    /// <summary>The maximum length of a merge-group id segment.</summary>
    public const int MaxLength = 31;

    /// <summary>The Rev 4 grammar regex for a single segment.</summary>
    public const string GrammarPattern = "^[a-z][a-z0-9-]{0,30}$";

    [GeneratedRegex(GrammarPattern, RegexOptions.CultureInvariant)]
    private static partial Regex GrammarRegex();

    /// <summary>The validated id text.</summary>
    public string Value { get; }

    private MergeGroupId(string value) => this.Value = value;

    /// <summary>
    /// Validates and wraps a string as a <see cref="MergeGroupId"/>. Throws
    /// <see cref="FormatException"/> when the input violates the Rev 4 grammar.
    /// </summary>
    public static MergeGroupId Parse(string value)
    {
        if (TryParse(value, out var id))
        {
            return id;
        }

        throw new FormatException(
            $"'{value}' is not a valid merge-group id segment. " +
            $"Grammar: {GrammarPattern} (lowercase letter then up to 30 " +
            $"lowercase alphanumerics or hyphens; '_' is forbidden because " +
            $"it is the merge-group hierarchy delimiter).");
    }

    /// <summary>
    /// Non-throwing variant for parsing from external input. Returns
    /// <c>false</c> when <paramref name="value"/> is null, empty, or
    /// fails the Rev 4 grammar.
    /// </summary>
    public static bool TryParse(string? value, out MergeGroupId id)
    {
        if (string.IsNullOrEmpty(value) || !GrammarRegex().IsMatch(value))
        {
            id = default;
            return false;
        }

        id = new MergeGroupId(value);
        return true;
    }

    /// <summary>Returns the validated id text.</summary>
    public override string ToString() => this.Value ?? string.Empty;
}
