namespace Polyphony.Branching;

/// <summary>
/// The work-item id of the run's apex (focus) item. Always positive, since
/// it indexes a real Azure DevOps work item. Distinct from <see cref="WorkItemId"/>
/// to make method signatures self-documenting at the call site
/// (e.g. <c>Build(rootId, itemId)</c> reads better than <c>Build(int, int)</c>
/// and the compiler refuses to swap the arguments).
/// </summary>
internal readonly record struct RootId
{
    /// <summary>The underlying integer id.</summary>
    public int Value { get; }

    private RootId(int value) => this.Value = value;

    /// <summary>
    /// Wraps a positive integer as a <see cref="RootId"/>. Throws on
    /// non-positive values; the upstream cache and ADO never produce them.
    /// </summary>
    public static RootId Parse(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "RootId must be positive (work-item ids are 1-indexed in ADO).");
        }

        return new RootId(value);
    }

    /// <summary>
    /// Non-throwing variant for parsing from external input (manifest, branch
    /// ref, CLI arg). Returns <c>false</c> for non-positive values.
    /// </summary>
    public static bool TryParse(int value, out RootId rootId)
    {
        if (value <= 0)
        {
            rootId = default;
            return false;
        }

        rootId = new RootId(value);
        return true;
    }

    /// <summary>Returns the underlying integer as its decimal string form.</summary>
    public override string ToString() => this.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
