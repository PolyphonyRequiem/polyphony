using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Polyphony.Tests.TestFixtures;

/// <summary>
/// Fluent builder for <see cref="WorkItem"/> instances in Polyphony tests.
/// Defaults to type Issue in state "To Do" — override only what matters for each test.
/// </summary>
public sealed class WorkItemBuilder
{
    private int _id = 1;
    private string _type = "Issue";
    private string _title = "Test Item";
    private string _state = "To Do";
    private string? _tags;
    private int? _parentId;
    private readonly List<WorkItemBuilder> _children = [];
    private readonly Dictionary<string, string?> _fields = [];

    public WorkItemBuilder WithId(int id) { _id = id; return this; }
    public WorkItemBuilder WithTitle(string title) { _title = title; return this; }
    public WorkItemBuilder WithType(string type) { _type = type; return this; }
    public WorkItemBuilder WithState(string state) { _state = state; return this; }
    public WorkItemBuilder WithParentId(int? parentId) { _parentId = parentId; return this; }
    public WorkItemBuilder WithTags(string tags) { _tags = tags; return this; }
    public WorkItemBuilder WithField(string name, string? value) { _fields[name] = value; return this; }

    /// <summary>
    /// Registers child builders whose items will have their ParentId set to this builder's Id.
    /// Use <see cref="BuildAll"/> to materialise the parent and all children.
    /// </summary>
    public WorkItemBuilder WithChildren(params WorkItemBuilder[] children)
    {
        _children.AddRange(children);
        return this;
    }

    /// <summary>Builds a single <see cref="WorkItem"/> (ignores registered children).</summary>
    public WorkItem Build()
    {
        var parsedType = WorkItemType.Parse(_type);
        if (!parsedType.IsSuccess)
            throw new InvalidOperationException($"Invalid work item type '{_type}': {parsedType.Error}");

        var item = new WorkItem
        {
            Id = _id,
            Type = parsedType.Value,
            Title = _title,
            ParentId = _parentId,
        };

        // State has internal set — use the public ChangeState() then clear dirty via MarkSynced().
        item.ChangeState(_state);
        if (_tags != null)
            item.UpdateField("System.Tags", _tags);
        item.MarkSynced(1);

        // Apply arbitrary fields after MarkSynced so they don't affect dirty state.
        foreach (var kvp in _fields)
        {
            item.UpdateField(kvp.Key, kvp.Value);
        }

        if (_fields.Count > 0)
            item.MarkSynced(1);

        return item;
    }

    /// <summary>
    /// Builds this item as the root plus all registered children.
    /// Each child's ParentId is forced to this builder's Id.
    /// </summary>
    public (WorkItem Root, IReadOnlyList<WorkItem> Children) BuildAll()
    {
        var root = Build();
        var children = new List<WorkItem>(_children.Count);

        foreach (var childBuilder in _children)
        {
            childBuilder.WithParentId(_id);
            children.Add(childBuilder.Build());
        }

        return (root, children);
    }
}
