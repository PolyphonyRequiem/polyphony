using System.Collections.Immutable;

namespace Polyphony.Branching;

/// <summary>
/// The hierarchical path of a merge group: a non-empty ordered list of
/// <see cref="MergeGroupId"/> segments joined by <c>_</c> in branch names
/// (per the Rev 4 grammar in <c>docs/decisions/branch-model.md</c>).
/// </summary>
/// <remarks>
/// Identity is the canonical <c>_</c>-joined string. Two paths with the
/// same canonical string are equal regardless of which segment instances
/// produced them; this matches the manifest invariant that <c>mg_path</c>
/// is the canonicalization input for both branch names and the topology
/// hash.
/// </remarks>
internal sealed class MergeGroupPath : IEquatable<MergeGroupPath>
{
    /// <summary>Depth at which a warning should be emitted on materialization.</summary>
    public const int WarningDepth = 3;

    /// <summary>Depth above which the driver hard-stops absent the
    /// <c>--allow-deep-nesting</c> override + recorded human approval.
    /// See branch-model ADR § Branch-name length cap &amp; nesting depth.</summary>
    public const int DefaultHardStopDepth = 5;

    private readonly ImmutableArray<MergeGroupId> segments;
    private readonly string canonical;

    private MergeGroupPath(ImmutableArray<MergeGroupId> segments)
    {
        this.segments = segments;
        this.canonical = string.Join('_', segments.Select(static s => s.Value));
    }

    /// <summary>The ordered list of segments from root to terminal.</summary>
    public IReadOnlyList<MergeGroupId> Segments => this.segments;

    /// <summary>The first (root-most) segment.</summary>
    public MergeGroupId Top => this.segments[0];

    /// <summary>The last (deepest) segment.</summary>
    public MergeGroupId Terminal => this.segments[^1];

    /// <summary>True when this path has exactly one segment.</summary>
    public bool IsTopLevel => this.segments.Length == 1;

    /// <summary>The number of segments in the path; equivalent to nesting depth.</summary>
    public int Depth => this.segments.Length;

    /// <summary>True when materialization should emit a warning per the ADR.</summary>
    public bool RequiresDepthWarning => this.Depth >= WarningDepth;

    /// <summary>True when materialization should hard-stop unless the
    /// caller has supplied <c>--allow-deep-nesting</c> + recorded approval.</summary>
    public bool ExceedsDefaultHardStopDepth => this.Depth > DefaultHardStopDepth;

    /// <summary>
    /// The canonical <c>_</c>-joined form. This string is the identity of
    /// the path: it is what appears in branch refs, what feeds the topology
    /// hash, and what the manifest stores in <c>mg_path</c>.
    /// </summary>
    public string Canonical => this.canonical;

    /// <summary>
    /// Constructs a top-level path (single segment) from a single id.
    /// </summary>
    public static MergeGroupPath Top1(MergeGroupId top) =>
        new(ImmutableArray.Create(top));

    /// <summary>
    /// Constructs a path from an ordered sequence of segments. Throws
    /// <see cref="ArgumentException"/> when the sequence is empty.
    /// </summary>
    public static MergeGroupPath Of(IEnumerable<MergeGroupId> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var array = segments.ToImmutableArray();
        if (array.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "MergeGroupPath requires at least one segment.",
                nameof(segments));
        }

        return new MergeGroupPath(array);
    }

    /// <summary>
    /// Constructs a path from a parameter list of segments.
    /// </summary>
    public static MergeGroupPath Of(params MergeGroupId[] segments) =>
        Of((IEnumerable<MergeGroupId>)segments);

    /// <summary>
    /// Returns a new path that extends this one with an additional terminal
    /// segment. Pure: this instance is unchanged.
    /// </summary>
    public MergeGroupPath Push(MergeGroupId nested) =>
        new(this.segments.Add(nested));

    /// <summary>
    /// Parses the canonical <c>_</c>-joined form back into a path. Throws
    /// <see cref="FormatException"/> on empty input or any invalid segment.
    /// </summary>
    public static MergeGroupPath Parse(string canonical)
    {
        if (TryParse(canonical, out var path) && path is not null)
        {
            return path;
        }

        throw new FormatException(
            $"'{canonical}' is not a valid merge-group path. " +
            $"Expected one or more '_'-joined segments, each matching " +
            $"{MergeGroupId.GrammarPattern}.");
    }

    /// <summary>
    /// Non-throwing variant. Returns <c>false</c> on empty input or any
    /// invalid segment.
    /// </summary>
    public static bool TryParse(string? canonical, out MergeGroupPath? path)
    {
        if (string.IsNullOrEmpty(canonical))
        {
            path = null;
            return false;
        }

        var rawSegments = canonical.Split('_');
        var builder = ImmutableArray.CreateBuilder<MergeGroupId>(rawSegments.Length);
        foreach (var raw in rawSegments)
        {
            if (!MergeGroupId.TryParse(raw, out var id))
            {
                path = null;
                return false;
            }

            builder.Add(id);
        }

        path = new MergeGroupPath(builder.MoveToImmutable());
        return true;
    }

    /// <summary>Returns <see cref="Canonical"/>.</summary>
    public override string ToString() => this.canonical;

    /// <inheritdoc />
    public bool Equals(MergeGroupPath? other) =>
        other is not null && string.Equals(this.canonical, other.canonical, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MergeGroupPath other && this.Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(this.canonical);

    /// <summary>Equality operator using canonical-string identity.</summary>
    public static bool operator ==(MergeGroupPath? left, MergeGroupPath? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Inequality operator using canonical-string identity.</summary>
    public static bool operator !=(MergeGroupPath? left, MergeGroupPath? right) => !(left == right);
}
