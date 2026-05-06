using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class PlanCommandsSeedChildrenTests : CommandTestBase
{
    private (PlanCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        return (new PlanCommands(walker, Repository, Config, twig), runner);
    }

    private static void StubShowTreeNoChildren(FakeProcessRunner runner, int parentId)
        => runner.WhenExact("twig", new[] { "show", parentId.ToString(), "--tree", "--output", "json" },
            new ProcessResult(0, $$"""{"id":{{parentId}},"title":"Parent","children":[]}""", ""));

    private static void StubShowTreeChildren(FakeProcessRunner runner, int parentId, string childrenJson)
        => runner.WhenExact("twig", new[] { "show", parentId.ToString(), "--tree", "--output", "json" },
            new ProcessResult(0, $$"""{"id":{{parentId}},"title":"Parent","children":{{childrenJson}}}""", ""));

    private static void StubShowParent(FakeProcessRunner runner, int parentId, string? tagsField)
    {
        var tags = tagsField is null ? "null" : $"\"{tagsField}\"";
        runner.WhenExact("twig", new[] { "show", parentId.ToString(), "--output", "json" },
            new ProcessResult(0, $$"""{"id":{{parentId}},"title":"Parent","tags":{{tags}}}""", ""));
    }

    private static void StubPatchOk(FakeProcessRunner runner)
        => runner.WhenStartsWith("twig", new[] { "patch" }, new ProcessResult(0, "{}", ""));

    private static void StubCreateChild(FakeProcessRunner runner, int newId)
        => runner.WhenStartsWith("twig", new[] { "new" },
            new ProcessResult(0, $$"""{"id":{{newId}},"title":"Created"}""", ""));

    [Fact]
    public async Task SeedChildren_EmptyChildren_StampsTagAndReturnsZeroCounts()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, "[]"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.WorkItemId.ShouldBe(100);
        result.ChildCount.ShouldBe(0);
        result.SeededCount.ShouldBe(0);
        result.ReusedCount.ShouldBe(0);
        result.ErrorCount.ShouldBe(0);
        result.PlannedTagSet.ShouldBeTrue();
        result.PlannedTagAlready.ShouldBeFalse();
    }

    [Fact]
    public async Task SeedChildren_TagAlreadyPresent_DoesNotPatch()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        StubShowParent(runner, 100, tagsField: "polyphony:planned; other-tag");
        // No patch stub — must NOT be called.

        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, "[]"));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.PlannedTagSet.ShouldBeTrue();
        result.PlannedTagAlready.ShouldBeTrue();

        var patchCalled = runner.Invocations.Any(i => i.Executable == "twig" && i.Arguments.Contains("patch"));
        patchCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task SeedChildren_NewChild_CreatesChildAndStampsTag()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        StubCreateChild(runner, newId: 555);
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var children = """[{"child_id":"task-1","title":"Do thing","type":"Task","description":"Body."}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.SeededCount.ShouldBe(1);
        result.SeededItems[0].WorkItemId.ShouldBe(555);
        result.SeededItems[0].MatchedBy.ShouldBe("created");

        // The marker must be embedded in the description sent to twig new.
        var createCall = runner.Invocations.First(i => i.Executable == "twig" && i.Arguments.Contains("new"));
        var descIdx = createCall.Arguments.ToList().IndexOf("--description");
        descIdx.ShouldBeGreaterThan(-1);
        createCall.Arguments[descIdx + 1].ShouldContain("<!-- polyphony:plan-child-id=task-1 -->");
    }

    [Fact]
    public async Task SeedChildren_MarkerMatch_ReusesExistingChildNoCreate()
    {
        var (cmd, runner) = CreateCommand();
        var existing = """[{"id":222,"type":"Task","title":"Existing","fields":{"System.Description":"body\n\n<!-- polyphony:plan-child-id=task-1 -->"}}]""";
        StubShowTreeChildren(runner, 100, existing);
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);
        // No StubCreateChild — must NOT be called.

        var children = """[{"child_id":"task-1","title":"Different Title","type":"Task","description":"Body."}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ReusedCount.ShouldBe(1);
        result.SeededCount.ShouldBe(0);
        result.ReusedItems[0].MatchedBy.ShouldBe("marker");
        result.ReusedItems[0].WorkItemId.ShouldBe(222);

        var createCalled = runner.Invocations.Any(i => i.Executable == "twig" && i.Arguments.Contains("new"));
        createCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task SeedChildren_TitleTypeFallback_ReusesAndAddsWarning()
    {
        var (cmd, runner) = CreateCommand();
        var existing = """[{"id":333,"type":"Task","title":"Do thing","fields":{"System.Description":"no marker here"}}]""";
        StubShowTreeChildren(runner, 100, existing);
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var children = """[{"child_id":"task-1","title":"Do thing","type":"Task","description":"Body."}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ReusedCount.ShouldBe(1);
        result.ReusedItems[0].MatchedBy.ShouldBe("title");
        result.ReusedItems[0].WorkItemId.ShouldBe(333);
        result.Warnings.Count.ShouldBe(1);
        result.Warnings[0].ShouldContain("title fallback");
    }

    [Fact]
    public async Task SeedChildren_ChildMissingId_RecordsErrorAndSkipsTagging()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        // No show / patch stubs for tagging — must NOT be called when errors > 0.

        var children = """[{"title":"Missing id","type":"Task","description":"Body."}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("child_id");
        result.PlannedTagSet.ShouldBeFalse();

        var patchCalled = runner.Invocations.Any(i => i.Executable == "twig" && i.Arguments.Contains("patch"));
        patchCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task SeedChildren_ChildMissingTitleOrType_RecordsError()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);

        var children = """[{"child_id":"task-1","description":"Body."}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].ChildId.ShouldBe("task-1");
        result.Errors[0].Error.ShouldContain("title or type");
    }

    [Fact]
    public async Task SeedChildren_AcceptanceCriteriaIncluded_FormatsAsBulletList()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        StubCreateChild(runner, newId: 555);
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var children = """[{"child_id":"task-1","title":"X","type":"Task","description":"Body.","acceptance_criteria":["AC one","AC two"]}]""";
        var (exit, _) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));
        exit.ShouldBe(ExitCodes.Success);

        var createCall = runner.Invocations.First(i => i.Executable == "twig" && i.Arguments.Contains("new"));
        var descIdx = createCall.Arguments.ToList().IndexOf("--description");
        var desc = createCall.Arguments[descIdx + 1];
        desc.ShouldContain("## Acceptance Criteria");
        desc.ShouldContain("- AC one");
        desc.ShouldContain("- AC two");
        desc.ShouldContain("<!-- polyphony:plan-child-id=task-1 -->");
    }

    [Fact]
    public async Task SeedChildren_BadJson_ReturnsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, "not json"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("valid JSON");
    }

    [Fact]
    public async Task SeedChildren_TwigShowTreeReturnsNothing_TreatedAsNoExistingChildren()
    {
        // ITwigClient.ShowTreeAsync returns null on non-zero exit — this is a
        // legitimate "no tree available" signal, not a hard failure. Verify we
        // proceed as if the parent had no existing children.
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("twig", new[] { "show", "100", "--tree", "--output", "json" },
            new ProcessResult(1, "", "boom"));
        StubCreateChild(runner, newId: 555);
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var children = """[{"child_id":"task-1","title":"X","type":"Task","description":"Body."}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.SeededCount.ShouldBe(1);
        result.SeededItems[0].WorkItemId.ShouldBe(555);
    }

    [Fact]
    public async Task SeedChildren_OutputIsSnakeCase()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var (_, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, "[]"));
        output.ShouldContain("\"work_item_id\"", Case.Sensitive);
        output.ShouldContain("\"child_count\"", Case.Sensitive);
        output.ShouldContain("\"seeded_count\"", Case.Sensitive);
        output.ShouldContain("\"planned_tag_set\"", Case.Sensitive);
        output.ShouldContain("\"planned_tag_already\"", Case.Sensitive);
        output.ShouldNotContain("\"WorkItemId\"", Case.Sensitive);
        output.ShouldNotContain("\"PlannedTagSet\"", Case.Sensitive);
    }
}
