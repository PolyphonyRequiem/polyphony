namespace Polyphony.Branching;

/// <summary>
/// A syntactically valid Polyphony branch ref under the Rev 4 grammar
/// (see <c>docs/decisions/branch-model.md</c>). The wrapper is opaque:
/// instances can only be produced by <see cref="BranchNameBuilder"/> or by
/// <see cref="BranchNameParser.TryParse(string, out ParsedBranch?)"/>, both of
/// which guarantee grammar conformance. Methods that operate on a
/// Polyphony branch ref take this type instead of <c>string</c> to make
/// it impossible to pass a slug, an arbitrary git ref, or an unsanitized
/// title to the wrong place.
/// </summary>
internal readonly record struct BranchName
{
    /// <summary>The validated branch ref text (e.g. <c>feature/1234</c>).</summary>
    public string Value { get; }

    private BranchName(string value) => this.Value = value;

    /// <summary>
    /// Internal factory used exclusively by <see cref="BranchNameBuilder"/>
    /// and <see cref="BranchNameParser"/> after they have validated the
    /// input against the grammar. Not exposed because callers should use
    /// the builder/parser.
    /// </summary>
    internal static BranchName CreateUnsafe(string validated) => new(validated);

    /// <summary>Returns <see cref="Value"/>.</summary>
    public override string ToString() => this.Value ?? string.Empty;
}
