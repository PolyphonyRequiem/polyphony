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

    // Pair helper for the sidecar-fallback tests (AB#3106 dogfood,
    // 2026-05-12). Writes a children-json sidecar to a fresh temp dir and
    // returns the absolute path the test can pass via --children-file.
    private static string WriteTempChildrenSidecar(string contents)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"polyphony-sidecar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "plan.children.json");
        File.WriteAllText(path, contents);
        return path;
    }

    // ---- Sidecar-fallback resolution (AB#3106 dogfood, 2026-05-12) ----
    //
    // The architect emits children in workflow-runtime context only. On
    // re-entry (state_detector(awaiting_review|merged_unseeded) → seeder)
    // the CLI arg is empty. The verb's contract: fall back to the
    // children-json sidecar that write-plan committed alongside the plan
    // markdown. Tests pin down the resolution order, error envelopes, and
    // defensive extraction promised by the design.

    [Fact]
    public async Task SeedChildren_SidecarFallback_ConsumesSidecarChildren()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 200);
        StubShowParent(runner, 200, tagsField: "");
        StubPatchOk(runner);
        StubCreateChild(runner, 9001);

        var sidecar = WriteTempChildrenSidecar(
            """[{"child_id":"task-1","title":"From sidecar","type":"Task","description":"x"}]""");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(200, "", "polyphony:planned", "", ".polyphony-config", sidecar));
            exit.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.WorkItemId.ShouldBe(200);
            result.ChildCount.ShouldBe(1);
            result.SeededCount.ShouldBe(1);
            result.ErrorCount.ShouldBe(0);
            result.PlannedTagSet.ShouldBeTrue();
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(sidecar)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SeedChildren_CliWinsOverSidecar()
    {
        // Both CLI --children-json and sidecar are present. CLI must win
        // (resolution order #1) — the sidecar's child_id must NOT appear.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 201);
        StubShowParent(runner, 201, tagsField: "");
        StubPatchOk(runner);
        StubCreateChild(runner, 9002);

        var sidecar = WriteTempChildrenSidecar(
            """[{"child_id":"sidecar-only","title":"Sidecar","type":"Task"}]""");
        try
        {
            var cli = """[{"child_id":"cli-only","title":"CLI","type":"Task"}]""";
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(201, cli, "polyphony:planned", "", ".polyphony-config", sidecar));
            exit.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ChildCount.ShouldBe(1);
            result.SeededCount.ShouldBe(1);
            // Verify the seeded child came from CLI, not sidecar.
            var newCall = runner.Invocations.First(i =>
                i.Executable == "twig" && i.Arguments.Contains("new"));
            string.Join(" ", newCall.Arguments).ShouldContain("CLI");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(sidecar)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SeedChildren_SidecarMalformedJson_RoutesError()
    {
        var (cmd, _) = CreateCommand();
        var sidecar = WriteTempChildrenSidecar("[not valid json");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(202, "", "polyphony:planned", "", ".polyphony-config", sidecar));
            exit.ShouldBe(ExitCodes.ConfigError);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ErrorCount.ShouldBe(1);
            result.Errors[0].Error.ShouldContain("sidecar");
            result.Errors[0].Error.ShouldContain("not valid JSON");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(sidecar)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SeedChildren_CliExplicitNull_RejectedAsNonArray()
    {
        // Rubber-duck #3 (AB#3106 dogfood, 2026-05-12): explicit `null` on
        // the CLI must be rejected with the same shape as on write-plan.
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(220, "null"));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("got null");
    }

    [Fact]
    public async Task SeedChildren_SidecarExplicitNull_RejectedAsNonArray()
    {
        // Same as CLI but for the sidecar branch — a hand-edited sidecar
        // holding `null` shouldn't masquerade as "skipped".
        var (cmd, _) = CreateCommand();
        var sidecar = WriteTempChildrenSidecar("null");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(221, "", "polyphony:planned", "", ".polyphony-config", sidecar));
            exit.ShouldBe(ExitCodes.ConfigError);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ErrorCount.ShouldBe(1);
            result.Errors[0].Error.ShouldContain("got null");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(sidecar)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SeedChildren_SidecarNotArray_RoutesError()
    {
        var (cmd, _) = CreateCommand();
        var sidecar = WriteTempChildrenSidecar("""{"not":"array"}""");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(203, "", "polyphony:planned", "", ".polyphony-config", sidecar));
            exit.ShouldBe(ExitCodes.ConfigError);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ErrorCount.ShouldBe(1);
            result.Errors[0].Error.ShouldContain("must contain a JSON array");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(sidecar)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SeedChildren_SidecarEmpty_NoApexFacets_RoutesError_WithSidecarDiagnostic()
    {
        // Sidecar exists but is empty array AND no apex_facets → refusal,
        // but the message must name the sidecar so the operator knows
        // what's actually missing (vs the no-source case).
        var (cmd, _) = CreateCommand();
        var sidecar = WriteTempChildrenSidecar("[]");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(204, "", "polyphony:planned", "", ".polyphony-config", sidecar));
            exit.ShouldBe(ExitCodes.ConfigError);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ErrorCount.ShouldBe(1);
            result.Errors[0].Error.ShouldContain("sidecar");
            result.Errors[0].Error.ShouldContain("empty array");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(sidecar)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SeedChildren_NoCliNoSidecar_NoApexFacets_RoutesError_WithNoSourceDiagnostic()
    {
        // No CLI, no sidecar (childrenFile points nowhere), no
        // apex_facets → refusal with the "no source" diagnostic. Differs
        // from the empty-sidecar case in the wording so operators can tell
        // them apart.
        var (cmd, _) = CreateCommand();
        var nonexistent = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(205, "", "polyphony:planned", "", ".polyphony-config", nonexistent));
        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("no children-json was supplied");
    }

    [Fact]
    public async Task SeedChildren_SidecarChildren_PlusApexFacets_MutuallyExclusive()
    {
        // Same mutual-exclusion rule as CLI children + apex_facets — the
        // sidecar shouldn't be a back door around it.
        var (cmd, _) = CreateCommand();
        var planFile = WriteTempPlanFile(206, "---\napex_facets: [implementable]\n---\n");
        var sidecar = WriteTempChildrenSidecar(
            """[{"child_id":"x","title":"X","type":"Task"}]""");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(206, "", "polyphony:planned", planFile, ".polyphony-config", sidecar));
            exit.ShouldBe(ExitCodes.ConfigError);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ErrorCount.ShouldBe(1);
            result.Errors[0].Error.ShouldContain("mutually exclusive");
        }
        finally
        {
            File.Delete(planFile);
            try { Directory.Delete(Path.GetDirectoryName(sidecar)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SeedChildren_SidecarChildWithNumericTitle_RoutesPerChildSeedError()
    {
        // Defensive extraction (TryReadStringField). A hand-edited or
        // architect-broken sidecar with a non-string field must NOT crash
        // the verb out of the loop — it must surface as a per-child
        // SeedError with the rest of the children still processed (or, when
        // it's the only child, an error envelope with PlannedTag NOT set).
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 207);
        StubShowParent(runner, 207, tagsField: "");

        var sidecar = WriteTempChildrenSidecar(
            """[{"child_id":"c1","title":42,"type":"Task"}]""");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(207, "", "polyphony:planned", "", ".polyphony-config", sidecar));
            // Verb completes (no crash). Per-child error in envelope.
            exit.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ErrorCount.ShouldBeGreaterThan(0);
            result.SeededCount.ShouldBe(0);
            result.Errors[0].Error.ShouldContain("title or type");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(sidecar)!, recursive: true); } catch { }
        }
    }

    // ---- From-ref recovery (AB#3106 rubber-duck #1, 2026-05-12) ----
    //
    // After merge_plan_pr the sidecar lives in the merged ref (origin/main)
    // but NOT in the local worktree (MergePlanPr deliberately does not
    // pull/checkout post-merge per Rev 4.2). On re-entry, the seeder must
    // be able to recover the children list straight from the ref. Tests
    // pin the resolution-order precedence (CLI > sidecar > from-ref >
    // apex_facets) and the hard-vs-soft failure semantics:
    //   - file-not-at-ref → soft (fall through to apex_facets / refusal)
    //   - bad-ref / git-error → hard (ConfigError envelope)
    //   - present-but-malformed → hard (ConfigError envelope)
    //   - present-but-not-array → hard (ConfigError envelope)

    private static void StubGitShow(FakeProcessRunner runner, string refspec, string path, ProcessResult response)
        => runner.WhenExact("git", new[] { "show", $"{refspec}:{path}" }, response);

    private static void StubGitFetchOk(FakeProcessRunner runner, string remote, string branch)
        => runner.WhenExact("git", new[] { "fetch", remote, $"{branch}:refs/remotes/{remote}/{branch}" }, new ProcessResult(0, "", ""));

    [Fact]
    public async Task SeedChildren_FromRef_HappyPath_ConsumesRefChildren()
    {
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 300);
        StubShowParent(runner, 300, tagsField: "");
        StubPatchOk(runner);
        StubCreateChild(runner, 9100);
        StubGitFetchOk(runner, "origin", "main");
        StubGitShow(runner, "origin/main", "plans/plan-300.children.json",
            new ProcessResult(0,
                """[{"child_id":"task-1","title":"From ref","type":"Task","description":"x"}]""",
                ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(300, "", "polyphony:planned", "", ".polyphony-config", "", "origin/main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.WorkItemId.ShouldBe(300);
        result.ChildCount.ShouldBe(1);
        result.SeededCount.ShouldBe(1);
        result.PlannedTagSet.ShouldBeTrue();

        // Verify the seeded child came from the ref payload.
        var newCall = runner.Invocations.First(i =>
            i.Executable == "twig" && i.Arguments.Contains("new"));
        string.Join(" ", newCall.Arguments).ShouldContain("From ref");
    }

    [Fact]
    public async Task SeedChildren_FromRef_FetchFailureSwallowed_ShowStillRuns()
    {
        // Best-effort fetch — if it fails (network down, ref already
        // local-only, etc.) the verb proceeds straight to `git show`. If
        // show then succeeds, the recovery completes normally.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 301);
        StubShowParent(runner, 301, tagsField: "");
        StubPatchOk(runner);
        StubCreateChild(runner, 9101);
        // git fetch returns nonzero -> GitClient throws -> seeder captures
        // the message and proceeds to git show. The captured fetch error is
        // included in the show-time diagnostic IF show then fails — in this
        // test, show succeeds so the swallow remains silent.
        runner.WhenExact("git", new[] { "fetch", "origin", "main:refs/remotes/origin/main" },
            new ProcessResult(1, "", "fatal: unable to access 'origin'"));
        StubGitShow(runner, "origin/main", "plans/plan-301.children.json",
            new ProcessResult(0,
                """[{"child_id":"task-1","title":"After fetch fail","type":"Task"}]""",
                ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(301, "", "polyphony:planned", "", ".polyphony-config", "", "origin/main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.SeededCount.ShouldBe(1);
        result.PlannedTagSet.ShouldBeTrue();
    }

    [Fact]
    public async Task SeedChildren_FromRef_FileNotAtRef_FallsThroughToApexFacets()
    {
        // GitClient.ShowFileAtRefAsync returns null when the file does not
        // exist at the ref (the canonical "no sidecar yet" case). The verb
        // must fall through to apex_facets — soft, not a hard error.
        var (cmd, runner) = CreateCommand();
        StubGitFetchOk(runner, "origin", "main");
        runner.WhenExact("git", new[] { "show", "origin/main:plans/plan-302.children.json" },
            new ProcessResult(128, "",
                "fatal: path 'plans/plan-302.children.json' does not exist in 'origin/main'"));

        var planFile = WriteTempPlanFile(302, "---\napex_facets: [implementable]\n---\n");
        try
        {
            // Twig stubs for the indivisible-apex path: parent tree fetch
            // happens after facets resolution succeeds.
            StubShowTreeNoChildren(runner, 302);
            StubShowParent(runner, 302, tagsField: "");
            StubPatchOk(runner);

            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(302, "", "polyphony:planned", planFile, ".polyphony-config", "", "origin/main"));

            exit.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.ApexFacets.ShouldContain("implementable");
            result.FacetsTagSet.ShouldBeTrue();
        }
        finally
        {
            File.Delete(planFile);
        }
    }

    [Fact]
    public async Task SeedChildren_FromRef_MalformedJson_RoutesError()
    {
        var (cmd, runner) = CreateCommand();
        StubGitFetchOk(runner, "origin", "main");
        StubGitShow(runner, "origin/main", "plans/plan-303.children.json",
            new ProcessResult(0, "[not valid json", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(303, "", "polyphony:planned", "", ".polyphony-config", "", "origin/main"));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("git ref 'origin/main");
        result.Errors[0].Error.ShouldContain("not valid JSON");
    }

    [Fact]
    public async Task SeedChildren_FromRef_NotArray_RoutesError()
    {
        var (cmd, runner) = CreateCommand();
        StubGitFetchOk(runner, "origin", "main");
        StubGitShow(runner, "origin/main", "plans/plan-304.children.json",
            new ProcessResult(0, """{"not":"array"}""", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(304, "", "polyphony:planned", "", ".polyphony-config", "", "origin/main"));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("git ref 'origin/main");
        result.Errors[0].Error.ShouldContain("must contain a JSON array");
    }

    [Fact]
    public async Task SeedChildren_FromRef_ExplicitNull_RoutesError()
    {
        var (cmd, runner) = CreateCommand();
        StubGitFetchOk(runner, "origin", "main");
        StubGitShow(runner, "origin/main", "plans/plan-305.children.json",
            new ProcessResult(0, "null", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(305, "", "polyphony:planned", "", ".polyphony-config", "", "origin/main"));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("got null");
    }

    [Fact]
    public async Task SeedChildren_LocalSidecarWinsOverFromRef()
    {
        // Resolution order #2 beats #3. If a local sidecar exists, the
        // verb consumes it and never goes to git for the from-ref
        // recovery path (no git stubs registered).
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 306);
        StubShowParent(runner, 306, tagsField: "");
        StubPatchOk(runner);
        StubCreateChild(runner, 9106);

        var sidecar = WriteTempChildrenSidecar(
            """[{"child_id":"local","title":"Local sidecar wins","type":"Task"}]""");
        try
        {
            var (exit, output) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(306, "", "polyphony:planned", "", ".polyphony-config", sidecar, "origin/main"));

            exit.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
            result.SeededCount.ShouldBe(1);
            // No git invocations should have happened.
            runner.Invocations.ShouldNotContain(i => i.Executable == "git");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(sidecar)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SeedChildren_CliWinsOverFromRef()
    {
        // Resolution order #1 beats everything below.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 307);
        StubShowParent(runner, 307, tagsField: "");
        StubPatchOk(runner);
        StubCreateChild(runner, 9107);

        var cli = """[{"child_id":"cli","title":"CLI wins","type":"Task"}]""";
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(307, cli, "polyphony:planned", "", ".polyphony-config", "", "origin/main"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.SeededCount.ShouldBe(1);
        runner.Invocations.ShouldNotContain(i => i.Executable == "git");
    }

    [Fact]
    public async Task SeedChildren_FromRef_FetchAndShowBothFail_ErrorIncludesFetchContext()
    {
        // When fetch fails AND show subsequently fails, the operator must
        // see the fetch failure in the diagnostic — otherwise a stale or
        // missing remote-tracking ref looks like a bad-ref error
        // (rubber-duck #2 follow-up, 2026-05-12).
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", new[] { "fetch", "origin", "main:refs/remotes/origin/main" },
            new ProcessResult(1, "", "fatal: unable to access 'origin': Could not resolve host"));
        // Show fails with a real bad-ref error (not the file-missing
        // whitelist) — GitClient throws ExternalToolException.
        runner.WhenExact("git", new[] { "show", "origin/main:plans/plan-309.children.json" },
            new ProcessResult(128, "", "fatal: bad revision 'origin/main:plans/plan-309.children.json'"));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(309, "", "polyphony:planned", "", ".polyphony-config", "", "origin/main"));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(1);
        result.Errors[0].Error.ShouldContain("bad revision");
        result.Errors[0].Error.ShouldContain("best-effort `git fetch` also failed");
        result.Errors[0].Error.ShouldContain("Could not resolve host");
    }

    [Fact]
    public async Task SeedChildren_FromRef_LocalRefNoSlash_SkipsFetch()
    {
        // A bare ref (no '<remote>/<branch>' shape — e.g. a SHA, tag name,
        // or branch already in local) MUST NOT attempt a fetch. We don't
        // stub `git fetch`; if the verb invokes it, the FakeProcessRunner
        // throws and the test fails.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 308);
        StubShowParent(runner, 308, tagsField: "");
        StubPatchOk(runner);
        StubCreateChild(runner, 9108);
        StubGitShow(runner, "HEAD", "plans/plan-308.children.json",
            new ProcessResult(0,
                """[{"child_id":"local-ref","title":"From HEAD","type":"Task"}]""",
                ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(308, "", "polyphony:planned", "", ".polyphony-config", "", "HEAD"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.SeededCount.ShouldBe(1);
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "git" && i.Arguments.FirstOrDefault() == "fetch");
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
    public async Task SeedChildren_TwigReturnsOnlyMessageField_RecoversFromMessageFallback()
    {
        // AB#3075 dogfood (2026-05-11): twig new returned ONLY a message
        // field — no id, no url. The work items WERE created in ADO; we
        // just couldn't tell because the JSON had nothing structured.
        // Recovery: parse "Created #NNNN" out of the message text.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        runner.WhenStartsWith("twig", new[] { "new" },
            new ProcessResult(0,
                """{"message":"Created #3080 Archivist agent + decision schema (Task)"}""",
                ""));
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        var children = """[{"child_id":"task-1","title":"Archivist agent + decision schema","type":"Task","description":"Body."}]""";
        var (exit, output) = await CaptureConsoleAsync(() => cmd.SeedChildren(100, children));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanSeedChildrenResult)!;
        result.ErrorCount.ShouldBe(0);
        result.SeededCount.ShouldBe(1);
        result.SeededItems[0].WorkItemId.ShouldBe(3080);
        result.Warnings.Count.ShouldBe(1);
        result.Warnings[0].ShouldContain("recovered id by parsing 'Created #N'");
    }

    [Fact]
    public void ExtractCreatedId_OnlyMessageField_ParsesIdFromCreatedHash()
    {
        // Real payload from AB#3075 dogfood (2026-05-11).
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            """{"message":"Created #3080 Archivist agent + decision schema (Task)"}""")!;
        var id = PlanCommands.ExtractCreatedId(node, out var source);
        id.ShouldBe(3080);
        source.ShouldBe("message");
    }

    [Theory]
    [InlineData("Created #42 Foo", 42)]
    [InlineData("Created #999 something with (Task)", 999)]
    [InlineData("Already exists #42", 0)]                  // wrong verb — must not match
    [InlineData("Created task #42", 0)]                    // missing the literal "Created #"
    [InlineData("created #42 lowercase", 42)]              // \b ensures word boundary; \s+ matches single space
    [InlineData("", 0)]
    public void ExtractCreatedId_MessageVariants(string message, int expected)
    {
        var encoded = JsonSerializer.Serialize(message);
        var node = System.Text.Json.Nodes.JsonNode.Parse($$"""{"message":{{encoded}}}""")!;
        var id = PlanCommands.ExtractCreatedId(node, out var source);
        id.ShouldBe(expected);
        source.ShouldBe(expected == 0 ? "none" : "message");
    }

    [Fact]
    public void ExtractCreatedId_StructuredIdWinsOverMessage()
    {
        // If twig returns BOTH a structured id AND a created-message, the
        // structured id takes precedence (regression guard).
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            """{"id":7777,"message":"Created #3080 Foo (Task)"}""")!;
        var id = PlanCommands.ExtractCreatedId(node, out var source);
        id.ShouldBe(7777);
        source.ShouldBe("id");
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

    // ---- Type template merge (B option) ----
    //
    // When a child type's template exists at
    // `{configDir}/work-item-types/templates/{typeslug}-template.md`, the
    // seeder uses it as the description scaffold and slots architect content
    // into known sections (first narrative section's placeholder body for the
    // free-form description, `## Acceptance Criteria` placeholders for AC
    // items). Other template placeholders pass through as TODOs for the
    // implementer. Without a template, the legacy verbatim+AC behaviour
    // stands.
    //
    // AB#3077 (2026-05-11) was the motivating dogfood — child Issues had
    // brief unstructured descriptions despite issue-template.md existing.

    private static string WriteTempTemplate(string typeSlug, string templateBody)
    {
        var configDir = Path.Combine(Path.GetTempPath(), $"polyphony-tmpl-{Guid.NewGuid():N}");
        var templateDir = Path.Combine(configDir, "work-item-types", "templates");
        Directory.CreateDirectory(templateDir);
        File.WriteAllText(Path.Combine(templateDir, $"{typeSlug}-template.md"), templateBody);
        return configDir;
    }

    [Fact]
    public async Task SeedChildren_TemplateExists_AppliesScaffoldAndSlotsArchitectContent()
    {
        var configDir = WriteTempTemplate("task", string.Join("\n", new[]
        {
            "## What to Change",
            "<Specific files, classes, methods to modify.>",
            "",
            "## How to Change",
            "<Implementation approach.>",
            "",
            "## Acceptance Criteria",
            "- [ ] <Task-specific criterion>",
            "- [ ] Build passes with zero errors and warnings",
            "- [ ] Relevant tests pass",
            "",
            "## Context (optional)",
            "<Dependencies, gotchas, related code paths.>",
        }));

        try
        {
            var (cmd, runner) = CreateCommand();
            StubShowTreeNoChildren(runner, 100);
            StubCreateChild(runner, newId: 555);
            StubShowParent(runner, 100, tagsField: "");
            StubPatchOk(runner);

            var children = """[{"child_id":"task-1","title":"X","type":"Task","description":"Wire up the foo module to the bar pipeline.","acceptance_criteria":["Foo emits BarEvent","Bar accepts FooEvent"]}]""";
            var (exit, _) = await CaptureConsoleAsync(
                () => cmd.SeedChildren(100, children, "polyphony:planned", "", configDir));
            exit.ShouldBe(ExitCodes.Success);

            var createCall = runner.Invocations.First(i => i.Executable == "twig" && i.Arguments.Contains("new"));
            var descIdx = createCall.Arguments.ToList().IndexOf("--description");
            var desc = createCall.Arguments[descIdx + 1];

            // Template scaffold preserved.
            desc.ShouldContain("## What to Change");
            desc.ShouldContain("## How to Change");
            desc.ShouldContain("## Acceptance Criteria");
            desc.ShouldContain("## Context (optional)");

            // Architect's free-form description slotted into first narrative section.
            desc.ShouldContain("Wire up the foo module to the bar pipeline.");
            desc.ShouldNotContain("<Specific files, classes, methods to modify.>");

            // Architect's AC items rendered as checkboxes.
            desc.ShouldContain("- [ ] Foo emits BarEvent");
            desc.ShouldContain("- [ ] Bar accepts FooEvent");
            // Placeholder AC item dropped.
            desc.ShouldNotContain("<Task-specific criterion>");
            // Hardcoded template AC items preserved.
            desc.ShouldContain("- [ ] Build passes with zero errors and warnings");
            desc.ShouldContain("- [ ] Relevant tests pass");

            // Other placeholders remain as TODOs for the implementer.
            desc.ShouldContain("<Implementation approach.>");
            desc.ShouldContain("<Dependencies, gotchas, related code paths.>");

            // Marker still at the bottom.
            desc.ShouldEndWith("<!-- polyphony:plan-child-id=task-1 -->");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.Combine(configDir, "work-item-types", "templates", "task-template.md"))))!, recursive: true);
        }
    }

    [Fact]
    public async Task SeedChildren_NoTemplateForType_FallsBackToVerbatimDescription()
    {
        // No templates directory exists — legacy behaviour: architect's
        // description verbatim, AC as plain bullets (not checkboxes), marker.
        var (cmd, runner) = CreateCommand();
        StubShowTreeNoChildren(runner, 100);
        StubCreateChild(runner, newId: 555);
        StubShowParent(runner, 100, tagsField: "");
        StubPatchOk(runner);

        // Point configDir at a non-existent path so the template lookup misses.
        var nonexistentConfig = Path.Combine(Path.GetTempPath(), $"polyphony-no-tmpl-{Guid.NewGuid():N}");
        var children = """[{"child_id":"task-1","title":"X","type":"Task","description":"Body.","acceptance_criteria":["AC one"]}]""";
        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.SeedChildren(100, children, "polyphony:planned", "", nonexistentConfig));
        exit.ShouldBe(ExitCodes.Success);

        var createCall = runner.Invocations.First(i => i.Executable == "twig" && i.Arguments.Contains("new"));
        var descIdx = createCall.Arguments.ToList().IndexOf("--description");
        var desc = createCall.Arguments[descIdx + 1];

        desc.ShouldStartWith("Body.");
        desc.ShouldContain("## Acceptance Criteria");
        // No-template path uses plain `- ` bullets, not `- [ ]` checkboxes.
        desc.ShouldContain("- AC one");
        desc.ShouldNotContain("- [ ]");
        desc.ShouldEndWith("<!-- polyphony:plan-child-id=task-1 -->");
    }

    [Fact]
    public void ApplyTypeTemplate_NoNarrativePlaceholder_PrependsArchitectNotes()
    {
        // Template has been hand-edited to remove the `<...>` placeholder
        // bodies. Architect content must NOT be silently dropped — falls
        // back to prepending an Architect Notes section.
        var template = "## Pre-filled Section\nThis was filled by the template author.\n\n## Acceptance Criteria\n- [ ] <criterion>\n";
        var result = PlanCommands.ApplyTypeTemplate(template, "Architect's intent here.", new[] { "AC" });
        result.ShouldStartWith("## Architect Notes");
        result.ShouldContain("Architect's intent here.");
        result.ShouldContain("## Pre-filled Section");
        result.ShouldContain("- [ ] AC");
    }

    [Fact]
    public void ApplyTypeTemplate_NoAcSection_AppendsAcAtBottom()
    {
        // Template has no `## Acceptance Criteria` heading at all (rare —
        // task-template / issue-template both have one, but defensive).
        var template = "## What to Change\n<placeholder>\n";
        var result = PlanCommands.ApplyTypeTemplate(template, "body", new[] { "AC1", "AC2" });
        result.ShouldContain("body");
        result.ShouldContain("## Acceptance Criteria");
        result.ShouldContain("- [ ] AC1");
        result.ShouldContain("- [ ] AC2");
    }

    [Fact]
    public void ApplyTypeTemplate_EmptyArchitectBody_KeepsTemplatePlaceholders()
    {
        // Defensive: architect emitted no description. Template placeholders
        // should remain intact (as TODOs); no spurious "Architect Notes"
        // section gets prepended for an empty body.
        var template = "## Section\n<placeholder>\n\n## Acceptance Criteria\n- [ ] <crit>\n";
        var result = PlanCommands.ApplyTypeTemplate(template, "", new[] { "AC" });
        result.ShouldContain("<placeholder>");
        result.ShouldNotContain("## Architect Notes");
        result.ShouldContain("- [ ] AC");
    }

    [Fact]
    public void TryLoadTypeTemplate_PathTraversal_AndCaseInsensitiveSlug()
    {
        // Slug = lowercase + whitespace-to-dash. "User Story" → "user-story".
        // Verify slug derivation matches the load-type SlugifyType behaviour
        // so they look up the same file.
        var configDir = WriteTempTemplate("user-story", "## Test\n<x>\n");
        try
        {
            PlanCommands.TryLoadTypeTemplate(configDir, "User Story").ShouldNotBeNull();
            PlanCommands.TryLoadTypeTemplate(configDir, "user story").ShouldNotBeNull();
            PlanCommands.TryLoadTypeTemplate(configDir, "USER STORY").ShouldNotBeNull();
            PlanCommands.TryLoadTypeTemplate(configDir, "Bug").ShouldBeNull();
            PlanCommands.TryLoadTypeTemplate(configDir, "").ShouldBeNull();
        }
        finally
        {
            Directory.Delete(configDir, recursive: true);
        }
    }
}
