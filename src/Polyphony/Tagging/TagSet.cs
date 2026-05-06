using System.Collections;

namespace Polyphony.Tagging;

/// <summary>
/// Parsed view of an ADO <c>System.Tags</c> field. Tags are semicolon-delimited
/// in storage; this type:
///
/// <list type="bullet">
///   <item>parses the raw field (whitespace-tolerant, blank-skipping),</item>
///   <item>compares case-insensitively (matches ADO behaviour),</item>
///   <item>preserves first-seen casing for round-tripping,</item>
///   <item>writes back as <c>"a; b; c"</c> with single space after each semicolon.</item>
/// </list>
///
/// Mutations return a NEW TagSet — instances are immutable.
/// </summary>
public sealed class TagSet : IEnumerable<string>
{
    private readonly List<string> _tags;

    private TagSet(List<string> tags)
    {
        _tags = tags;
    }

    /// <summary>An empty tag set.</summary>
    public static TagSet Empty { get; } = new([]);

    /// <summary>
    /// Parses an ADO <c>System.Tags</c> raw value. Null / whitespace / empty
    /// returns <see cref="Empty"/>. Tokens are trimmed and blanks skipped;
    /// case-insensitive duplicates are folded (first-seen casing wins).
    /// </summary>
    public static TagSet Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var part in raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(part))
            {
                ordered.Add(part);
            }
        }

        return new TagSet(ordered);
    }

    /// <summary>Number of distinct tags.</summary>
    public int Count => _tags.Count;

    /// <summary>Case-insensitive contains check.</summary>
    public bool Contains(string tag) =>
        _tags.Exists(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns a new TagSet with <paramref name="tag"/> appended. If the tag is
    /// already present (case-insensitive), returns the SAME instance — callers
    /// can use referential equality to detect a no-op.
    /// </summary>
    public TagSet Add(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new ArgumentException("Tag cannot be null or whitespace.", nameof(tag));
        if (Contains(tag)) return this;

        var copy = new List<string>(_tags.Count + 1);
        copy.AddRange(_tags);
        copy.Add(tag.Trim());
        return new TagSet(copy);
    }

    /// <summary>
    /// Returns a new TagSet with <paramref name="tag"/> removed (case-insensitive).
    /// If the tag is absent, returns the SAME instance — callers can use
    /// referential equality to detect a no-op.
    /// </summary>
    public TagSet Remove(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return this;
        var idx = _tags.FindIndex(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return this;

        var copy = new List<string>(_tags.Count - 1);
        for (var i = 0; i < _tags.Count; i++)
        {
            if (i != idx) copy.Add(_tags[i]);
        }

        return new TagSet(copy);
    }

    /// <summary>
    /// Formats the tag set for writing to <c>System.Tags</c>. Empty set produces
    /// the empty string; otherwise uses <c>"; "</c> as the delimiter (matches
    /// ADO's display normalization).
    /// </summary>
    public string Format() => string.Join("; ", _tags);

    /// <summary>Snapshot the tags as a string array (preserves order).</summary>
    public string[] ToArray() => [.. _tags];

    public IEnumerator<string> GetEnumerator() => _tags.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
