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
        return (new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner), new FakePostconditionVerifier(), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(new GitClient(runner))), runner);
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
    public async Task SeedChildren_EmptyChildrenNoFacets_RoutesError()
    {
        // Indivisibility must be explicit. An empty children list with no
        // apex_facets declaration is ambiguous and would silently false-
        // satisfy the apex (AB#3064 dogfood, 2026-05-09). The verb refuses.
        var (cmd, _) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, "[]"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("apex_facets");
        result.PlannedTagSet.ShouldBeFalse();
    }

    [Fact]
    public async Task SeedChildren_EmptyChildrenWithApexFacets_StampsTags()
    {
        // The legitimate "decomposable but indivisible" case: planner emits
        // no children but declares apex_facets in plan front-matter.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var planFile = WriteTempPlanFile(100, "---\napex_facets: [implementable]\n---\n");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(100, "[]", "polyphony:planned", planFile));
            exit.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.WorkItemId.ShouldBe(100);
            result.ChildCount.ShouldBe(0);
            result.SeededCount.ShouldBe(0);
            result.ReusedCount.ShouldBe(0);
            result.ErrorCount.ShouldBe(0);
            result.PlannedTagSet.ShouldBeTrue();
            result.PlannedTagAlready.ShouldBeFalse();
            result.FacetsTagSet.ShouldBeTrue();
        }
        finally
        {
            File.Delete(planFile);
        }
    }

    [Fact]
    public async Task SeedChildren_TagAlreadyPresent_DoesNotPatch()
    {
        // Idempotency: re-running with all children already present + tag
        // already on parent should NOT patch. Using non-empty children with
        // a marker-match so reuse path triggers.
        var (cmd, runner) = CreateCommand();
        const string existing = """[{"id":555,"title":"Do thing","type":"Task","description":"x\n<!-- polyphony:plan-child-id=task-1 -->"}]""";
        StubShowTreeChildren(runner, 100, existing);
        StubShowParent(runner, 100, tagsField: "polyphony:planned; other-tag");
        // No patch stub — must NOT be called.

        var children = """[{"child_id":"task-1","title":"Do thing","type":"Task","description":"x"}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.PlannedTagSet.ShouldBeTrue();
        result.PlannedTagAlready.ShouldBeTrue();
        result.ReusedCount.ShouldBe(1);

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

        var planFile = WriteTempPlanFile(100, "---\napex_facets: [implementable]\n---\n");
        try
        {
            var (_, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(100, "[]", "polyphony:planned", planFile));
            output.ShouldContain("\"work_item_id\"", Case.Sensitive);
            output.ShouldContain("\"child_count\"", Case.Sensitive);
            output.ShouldContain("\"seeded_count\"", Case.Sensitive);
            output.ShouldContain("\"planned_tag_set\"", Case.Sensitive);
            output.ShouldContain("\"planned_tag_already\"", Case.Sensitive);
            output.ShouldContain("\"apex_facets\"", Case.Sensitive);
            output.ShouldContain("\"facets_tag_set\"", Case.Sensitive);
            output.ShouldNotContain("\"WorkItemId\"", Case.Sensitive);
            output.ShouldNotContain("\"PlannedTagSet\"", Case.Sensitive);
        }
        finally
        {
            File.Delete(planFile);
        }
    }

    // ── apex_facets / plan front-matter (closed-loop PR #7) ─────────────

    [Fact]
    public async Task SeedChildren_ApexFacetsFrontMatter_NoChildren_StampsFacetsTag()
    {
        // Architect declares apex_facets in plan front-matter and emits no
        // children — the apex is "indivisible". The seed-children verb must
        // stamp polyphony:facets=... on the parent so downstream consumers
        // see the override.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var planFile = WriteTempPlanFile(100, "---\napex_facets: [implementable]\n---\n# Plan for #100\n");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(100, "[]", "polyphony:planned", planFile));
            exit.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ChildCount.ShouldBe(0);
            result.ApexFacets.ShouldBe(["implementable"]);
            result.FacetsTagSet.ShouldBeTrue();
            result.PlannedTagSet.ShouldBeTrue();

            // The patch invocation must include the canonical facets tag.
            var patchCall = runner.Invocations.First(i =>
                i.Executable == "twig" && i.Arguments.Contains("patch"));
            string.Join(" ", patchCall.Arguments).ShouldContain("polyphony:facets=implementable");
        }
        finally
        {
            File.Delete(planFile);
        }
    }

    [Fact]
    public async Task SeedChildren_ApexFacetsFrontMatter_WithChildren_RoutesError()
    {
        // apex_facets and a non-empty children list say opposite things;
        // the verb must refuse rather than guess.
        var (cmd, _) = CreateCommand();
        var planFile = WriteTempPlanFile(101, "---\napex_facets: [implementable]\n---\n");
        try
        {
            var children = """[{"child_id":"task-1","title":"X","type":"Task","description":"Body."}]""";
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(101, children, "polyphony:planned", planFile));
            exit.ShouldBe(ExitCodes.ConfigError);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ErrorCount.ShouldBe(1);
            result.Errors[0].Error.ShouldContain("mutually exclusive");
        }
        finally
        {
            File.Delete(planFile);
        }
    }

    [Fact]
    public async Task SeedChildren_PlanFileMissing_NoChildren_RoutesError()
    {
        // Default planFile path is plans/plan-{id}.md relative to cwd; when
        // it doesn't exist AND children-json is empty, the strict
        // indivisibility-must-be-explicit check fires (AB#3064 dogfood).
        var (cmd, _) = CreateCommand();

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(102, "[]", "polyphony:planned",
                Path.Combine(Path.GetTempPath(), $"polyphony-pr7-missing-{Guid.NewGuid():N}.md")));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("apex_facets");
        result.PlannedTagSet.ShouldBeFalse();
    }

    [Fact]
    public async Task SeedChildren_PlanFileMissing_WithChildren_BehavesAsIfFrontMatterAbsent()
    {
        // Missing plan file with non-empty children is the classic flow:
        // planner emitted children, no front-matter to read, just stamp the
        // planned tag and reconcile the children.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 102);
        StubCreateChild(runner, newId: 777);
        StubShowParent(runner, 102, tagsField: "");
        StubPatchOk(runner);

        var children = """[{"child_id":"task-1","title":"Do the thing","type":"Task","description":"…"}]""";
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(102, children, "polyphony:planned",
                Path.Combine(Path.GetTempPath(), $"polyphony-pr7-missing-{Guid.NewGuid():N}.md")));
        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ApexFacets.ShouldBeEmpty();
        result.FacetsTagSet.ShouldBeFalse();
        result.PlannedTagSet.ShouldBeTrue();
        result.SeededCount.ShouldBe(1);
    }

    [Fact]
    public async Task SeedChildren_PlanFileMalformed_RoutesError()
    {
        var (cmd, _) = CreateCommand();
        var planFile = WriteTempPlanFile(103, "---\napex_facets:\n  - bogus\n---\n");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(103, "[]", "polyphony:planned", planFile));
            exit.ShouldBe(ExitCodes.ConfigError);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ErrorCount.ShouldBe(1);
            result.Errors[0].Error.ShouldContain("malformed");
        }
        finally
        {
            File.Delete(planFile);
        }
    }

    [Fact]
    public async Task SeedChildren_ApexFacetsReplacesExistingFacetsTag()
    {
        // Re-plan with a different facet set should replace, not stack —
        // otherwise the parent ends up with two polyphony:facets=... tags
        // and TryExtract picks an arbitrary one.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 104);
        StubShowParent(runner, 104, tagsField: "polyphony:facets=plannable; other-tag");
        StubPatchOk(runner);

        var planFile = WriteTempPlanFile(104, "---\napex_facets: [implementable]\n---\n");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(104, "[]", "polyphony:planned", planFile));
            exit.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.FacetsTagSet.ShouldBeTrue();

            var patchCall = runner.Invocations.First(i =>
                i.Executable == "twig" && i.Arguments.Contains("patch"));
            var combined = string.Join(" ", patchCall.Arguments);
            combined.ShouldContain("polyphony:facets=implementable");
            combined.ShouldNotContain("polyphony:facets=plannable");
            combined.ShouldContain("other-tag");
        }
        finally
        {
            File.Delete(planFile);
        }
    }

    private static string WriteTempPlanFile(int workItemId, string body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"polyphony-pr7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"plan-{workItemId}.md");
        File.WriteAllText(path, body);
        return path;
    }

    // ---- ExtractCreatedId hardening (AB#3071 dogfood, 2026-05-10) ----
    //
    // Six children seeded successfully into ADO but the seeder emitted
    // "twig new returned no id" for all six because twig's JSON payload
    // came back with an unusable id. The two paths verified by these tests:
    //
    //   1. Url fallback recovers the id and the seeder records a warning
    //      so we know the upstream race is still happening.
    //   2. When both id AND url fail, the error message includes the raw
    //      payload so the next occurrence is self-diagnosing.

    [Fact]
    public async Task SeedChildren_TwigReturnsZeroId_RecoversFromUrlFallback()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        // Twig emits id:0 (the AB#3071 dogfood symptom — likely from a
        // post-create FetchAsync race in twig itself) but the url is
        // correct because it's built from the actual create-response id.
        runner.WhenStartsWith("twig", new[] { "new" },
            new ProcessResult(0,
                """{"id":0,"type":"Task","title":"x","parent":100,"url":"https://dev.azure.com/org/proj/_workitems/edit/777"}""",
                ""));
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var children = """[{"child_id":"task-1","title":"x","type":"Task","description":"Body."}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(0);
        result.SeededCount.ShouldBe(1);
        result.SeededItems[0].WorkItemId.ShouldBe(777);
        result.SeededItems[0].MatchedBy.ShouldBe("created");
        result.Warnings.Count.ShouldBe(1);
        result.Warnings[0].ShouldContain("recovered id from url");
    }

    [Fact]
    public async Task SeedChildren_TwigReturnsNoIdAndNoUrl_RecordsRawPayloadInError()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        // Both paths fail — neither id nor a usable url. The error message
        // must include the raw payload so we don't have to scrape event
        // logs to figure out what twig actually returned.
        runner.WhenStartsWith("twig", new[] { "new" },
            new ProcessResult(0, """{"unexpected":"shape","totally":"different"}""", ""));

        var children = """[{"child_id":"task-1","title":"x","type":"Task","description":"Body."}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("twig new returned no usable id");
        result.Errors[0].Error.ShouldContain("raw payload");
        result.Errors[0].Error.ShouldContain("\"unexpected\":\"shape\"");
    }

    [Fact]
    public async Task SeedChildren_TwigReturnsStringId_FallsBackToUrlNotThrow()
    {
        // Defensive: if twig ever drifts to emitting id as a string, we don't
        // want a raw InvalidOperationException to crash the whole seed —
        // we want the url fallback to kick in transparently.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        runner.WhenStartsWith("twig", new[] { "new" },
            new ProcessResult(0,
                """{"id":"888","url":"https://dev.azure.com/org/proj/_workitems/edit/888"}""",
                ""));
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var children = """[{"child_id":"task-1","title":"x","type":"Task","description":"Body."}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.SeededCount.ShouldBe(1);
        result.SeededItems[0].WorkItemId.ShouldBe(888);
    }

    [Fact]
    public void ExtractCreatedId_HappyPath_ReturnsIdFromIdField()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            """{"id":3072,"url":"https://dev.azure.com/org/proj/_workitems/edit/3072"}""")!;
        var id = PlanCommands.ExtractCreatedId(node, out var source);
        id.ShouldBe(3072);
        source.ShouldBe("id");
    }

    [Theory]
    [InlineData("https://dev.azure.com/org/proj/_workitems/edit/3072", 3072)]
    [InlineData("https://dev.azure.com/org/proj/_workitems/edit/3072/", 3072)]
    [InlineData("https://dev.azure.com/org/proj/_workitems/edit/3072?foo=bar", 3072)]
    [InlineData("https://github.com/o/r/issues/42", 0)]
    [InlineData("not a url", 0)]
    [InlineData("", 0)]
    public void ExtractCreatedId_ZeroIdWithUrl_ParsesIdFromUrl(string url, int expected)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse($$"""{"id":0,"url":"{{url}}"}""")!;
        var id = PlanCommands.ExtractCreatedId(node, out var source);
        id.ShouldBe(expected);
        source.ShouldBe(expected == 0 ? "none" : "url");
    }

    [Fact]
    public void ExtractCreatedId_BothMissing_ReturnsZero()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"unrelated":"shape"}""")!;
        var id = PlanCommands.ExtractCreatedId(node, out var source);
        id.ShouldBe(0);
        source.ShouldBe("none");
    }

    [Fact]
    public void TruncateForError_ShortString_ReturnedAsIs()
    {
        PlanCommands.TruncateForError("hello").ShouldBe("hello");
    }

    [Fact]
    public void TruncateForError_LongString_TruncatedWithEllipsis()
    {
        var raw = new string('x', 600);
        var truncated = PlanCommands.TruncateForError(raw, max: 500);
        truncated.Length.ShouldBe(501); // 500 chars + 1-char ellipsis
        truncated.ShouldEndWith("…");
    }
}
