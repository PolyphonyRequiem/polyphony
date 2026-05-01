using Polyphony.Configuration;
using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Cross-process template tests for <see cref="TransitionValidator"/>.
/// Validates that lifecycle event transitions route correctly across
/// Basic, Agile, Scrum, and CMMI process templates.
/// </summary>
public sealed class CrossProcessTransitionValidatorTests
{
    /// <summary>
    /// Defines the transition table and state names for a single process template.
    /// </summary>
    private sealed record TransitionTemplate(
        string Name,
        string TopType,
        string MiddleType,
        string LeafType,
        string ProposedState,
        string InProgressState,
        string CompletedState,
        Dictionary<string, Dictionary<string, string>> Transitions);

    private static readonly TransitionTemplate[] Templates =
    [
        new("Basic",
            TopType: "Epic",
            MiddleType: "Issue",
            LeafType: "Task",
            ProposedState: "To Do",
            InProgressState: "Doing",
            CompletedState: "Done",
            Transitions: new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Epic"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_planning"] = "Doing",
                    ["all_children_complete"] = "Done",
                },
                ["Issue"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_planning"] = "Doing",
                    ["begin_implementation"] = "Doing",
                    ["implementation_complete"] = "Done",
                    ["all_children_complete"] = "Done",
                },
                ["Task"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_implementation"] = "Doing",
                    ["implementation_complete"] = "Done",
                },
            }),
        new("Agile",
            TopType: "Epic",
            MiddleType: "User Story",
            LeafType: "Task",
            ProposedState: "New",
            InProgressState: "Active",
            CompletedState: "Closed",
            Transitions: new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Epic"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_planning"] = "Active",
                    ["all_children_complete"] = "Closed",
                },
                ["User Story"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_planning"] = "Active",
                    ["begin_implementation"] = "Active",
                    ["implementation_complete"] = "Closed",
                    ["all_children_complete"] = "Closed",
                },
                ["Task"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_implementation"] = "Active",
                    ["implementation_complete"] = "Closed",
                },
            }),
        new("Scrum",
            TopType: "Epic",
            MiddleType: "Product Backlog Item",
            LeafType: "Task",
            ProposedState: "New",
            InProgressState: "Committed",
            CompletedState: "Done",
            Transitions: new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Epic"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_planning"] = "Committed",
                    ["all_children_complete"] = "Done",
                },
                ["Product Backlog Item"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_planning"] = "Committed",
                    ["begin_implementation"] = "Committed",
                    ["implementation_complete"] = "Done",
                    ["all_children_complete"] = "Done",
                },
                ["Task"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_implementation"] = "Committed",
                    ["implementation_complete"] = "Done",
                },
            }),
        new("CMMI",
            TopType: "Epic",
            MiddleType: "Requirement",
            LeafType: "Task",
            ProposedState: "Proposed",
            InProgressState: "Active",
            CompletedState: "Closed",
            Transitions: new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Epic"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_planning"] = "Active",
                    ["all_children_complete"] = "Closed",
                },
                ["Requirement"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_planning"] = "Active",
                    ["begin_implementation"] = "Active",
                    ["implementation_complete"] = "Closed",
                    ["all_children_complete"] = "Closed",
                },
                ["Task"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["begin_implementation"] = "Active",
                    ["implementation_complete"] = "Closed",
                },
            }),
    ];

    public static IEnumerable<object[]> AllTemplateNames =>
        Templates.Select(t => new object[] { t.Name });

    private static TransitionTemplate GetTemplate(string name) =>
        Templates.First(t => t.Name == name);

    private static TransitionValidator CreateValidator(TransitionTemplate template)
    {
        var builder = new ProcessConfigBuilder()
            .WithProcessTemplate(template.Name);

        foreach (var (typeName, transitions) in template.Transitions)
        {
            string[] capabilities = typeName == template.TopType
                ? ["plannable"]
                : typeName == template.LeafType
                    ? ["implementable"]
                    : ["plannable", "implementable"];

            builder.WithType(typeName, capabilities, transitions);
        }

        return new TransitionValidator(builder.Build());
    }

    // ── begin_planning: happy path ───────────────────────────────────

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void BeginPlanning_TopLevel_WhenProposed_ReturnsValid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.ProposedState).Build();

        var result = validator.Validate(item, "begin_planning", []);

        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(t.Transitions[t.TopType]["begin_planning"]);
    }

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void BeginPlanning_MiddleType_WhenProposed_ReturnsValid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(2).WithType(t.MiddleType).WithState(t.ProposedState).Build();

        var result = validator.Validate(item, "begin_planning", []);

        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(t.Transitions[t.MiddleType]["begin_planning"]);
    }

    // ── begin_planning: precondition failure ─────────────────────────

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void BeginPlanning_WhenInProgress_ReturnsInvalid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.InProgressState).Build();

        var result = validator.Validate(item, "begin_planning", []);

        result.IsValid.ShouldBeFalse();
        result.Message!.ShouldContain("begin_planning");
        result.Message!.ShouldContain("Proposed");
    }

    // ── begin_implementation: happy path ─────────────────────────────

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void BeginImplementation_Leaf_WhenProposed_ReturnsValid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(10).WithType(t.LeafType).WithState(t.ProposedState).Build();

        var result = validator.Validate(item, "begin_implementation", []);

        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(t.Transitions[t.LeafType]["begin_implementation"]);
    }

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void BeginImplementation_Leaf_WhenInProgress_ReturnsValid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(10).WithType(t.LeafType).WithState(t.InProgressState).Build();

        var result = validator.Validate(item, "begin_implementation", []);

        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(t.Transitions[t.LeafType]["begin_implementation"]);
    }

    // ── begin_implementation: precondition failure ───────────────────

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void BeginImplementation_WhenCompleted_ReturnsInvalid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(10).WithType(t.LeafType).WithState(t.CompletedState).Build();

        var result = validator.Validate(item, "begin_implementation", []);

        result.IsValid.ShouldBeFalse();
        result.Message!.ShouldContain("begin_implementation");
        result.Message!.ShouldContain("Proposed or InProgress");
    }

    // ── implementation_complete: happy path ──────────────────────────

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void ImplementationComplete_Leaf_WhenInProgress_ReturnsValid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(10).WithType(t.LeafType).WithState(t.InProgressState).Build();

        var result = validator.Validate(item, "implementation_complete", []);

        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(t.Transitions[t.LeafType]["implementation_complete"]);
    }

    // ── implementation_complete: precondition failure ────────────────

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void ImplementationComplete_WhenProposed_ReturnsInvalid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(10).WithType(t.LeafType).WithState(t.ProposedState).Build();

        var result = validator.Validate(item, "implementation_complete", []);

        result.IsValid.ShouldBeFalse();
        result.Message!.ShouldContain("implementation_complete");
        result.Message!.ShouldContain("InProgress");
    }

    // ── all_children_complete: happy path ────────────────────────────

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void AllChildrenComplete_TopLevel_WhenAllDone_ReturnsValid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.InProgressState).Build();
        var child1 = new WorkItemBuilder().WithId(10).WithType(t.MiddleType).WithState(t.CompletedState).Build();
        var child2 = new WorkItemBuilder().WithId(11).WithType(t.MiddleType).WithState(t.CompletedState).Build();

        var result = validator.Validate(item, "all_children_complete", [child1, child2]);

        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(t.Transitions[t.TopType]["all_children_complete"]);
    }

    // ── all_children_complete: precondition failure ──────────────────

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void AllChildrenComplete_WhenChildNotDone_ReturnsInvalid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.InProgressState).Build();
        var child1 = new WorkItemBuilder().WithId(10).WithType(t.MiddleType).WithState(t.CompletedState).Build();
        var child2 = new WorkItemBuilder().WithId(11).WithType(t.MiddleType).WithState(t.InProgressState).Build();

        var result = validator.Validate(item, "all_children_complete", [child1, child2]);

        result.IsValid.ShouldBeFalse();
        result.Message!.ShouldContain("child #11");
    }

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void AllChildrenComplete_WhenNoChildren_ReturnsInvalid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.InProgressState).Build();

        var result = validator.Validate(item, "all_children_complete", []);

        result.IsValid.ShouldBeFalse();
        result.Message!.ShouldContain("no children");
    }

    // ── unknown event ───────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void UnknownEvent_ReturnsInvalid(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(1).WithType(t.TopType).WithState(t.ProposedState).Build();

        var result = validator.Validate(item, "nonexistent_event", []);

        result.IsValid.ShouldBeFalse();
        result.Message!.ShouldContain("Unknown event");
    }

    // ── target state correctness ────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllTemplateNames))]
    public void ValidTransition_IncludesCorrectTargetStateInResult(string templateName)
    {
        var t = GetTemplate(templateName);
        var validator = CreateValidator(t);
        var item = new WorkItemBuilder().WithId(10).WithType(t.LeafType).WithState(t.InProgressState).Build();

        var result = validator.Validate(item, "implementation_complete", []);

        result.IsValid.ShouldBeTrue();
        result.TargetState.ShouldBe(t.CompletedState);
        result.Message!.ShouldContain(t.CompletedState);
    }
}
