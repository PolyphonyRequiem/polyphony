namespace Polyphony.Branching;

/// <summary>
/// A non-root work-item id used as the trailing id segment of task, plan,
/// and evidence branch names. Always positive. Distinct from
/// <see cref="RootId"/> so that method signatures cannot accidentally
/// swap "the root" and "the item".
/// </summary>
internal readonly record struct WorkItemId
{
    /// <summary>The underlying integer id.</summary>
    public int Value { get; }

    private WorkItemId(int value) => this.Value = value;

    /// <summary>
    /// Wraps a positive integer as a <see cref="WorkItemId"/>. Throws on
    /// non-positive values; ADO never produces them.
    /// </summary>
    public static WorkItemId Parse(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "WorkItemId must be positive (work-item ids are 1-indexed in ADO).");
        }

        return new WorkItemId(value);
    }

    /// <summary>
    /// Non-throwing variant for parsing from external input. Returns
    /// <c>false</c> for non-positive values.
    /// </summary>
    public static bool TryParse(int value, out WorkItemId itemId)
    {
        if (value <= 0)
        {
            itemId = default;
            return false;
        }

        itemId = new WorkItemId(value);
        return true;
    }

    /// <summary>Returns the underlying integer as its decimal string form.</summary>
    public override string ToString() => this.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
