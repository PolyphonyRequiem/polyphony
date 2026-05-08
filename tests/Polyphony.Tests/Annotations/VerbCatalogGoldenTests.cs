using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Annotations;

/// <summary>
/// Golden tests that pin the structural shapes that the workflow-YAML
/// resolver lint (#175) will rely on. Each test reaches into a
/// specific field of the live <see cref="VerbOutputSchemaCatalog"/>
/// and asserts the metadata the lint will key off.
///
/// <para>These double as snapshot tests: a generator change that
/// silently drops <c>type_ref</c>, flips <c>can_omit_when_null</c>, or
/// reverts a per-property <c>[JsonIgnore(Condition=Never)]</c> override
/// will fail one of these.</para>
///
/// <para>Where the ADR's example used pre-namespace-collapse FQNs
/// (e.g. <c>Polyphony.Models.PlanDeriveAncestorChainResult</c>), the
/// actual catalog now uses the post-collapse FQN
/// (<c>Polyphony.PlanDeriveAncestorChainResult</c>) because the DTOs
/// declare <c>namespace Polyphony;</c> directly. Tests assert the
/// real, current FQN — they are the canonical wire shape.</para>
/// </summary>
public sealed class VerbCatalogGoldenTests
{
    private static readonly JsonObject Root =
        JsonNode.Parse(VerbOutputSchemaCatalog.Json)!.AsObject();

    private static JsonObject Types => Root["types"]!.AsObject();
    private static JsonObject Verbs => Root["verbs"]!.AsObject();

    private static JsonObject Field(string typeFqn, string fieldName)
    {
        var typeEntry = Types[typeFqn]?.AsObject();
        typeEntry.ShouldNotBeNull($"types['{typeFqn}'] is missing from the catalog");
        var fields = typeEntry!["fields"]!.AsArray();
        var field = fields.FirstOrDefault(f => f!.AsObject()["name"]!.GetValue<string>() == fieldName);
        field.ShouldNotBeNull($"field '{fieldName}' is missing from {typeFqn}");
        return field!.AsObject();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Golden 1 — per-property [JsonIgnore(Condition=Never)] override
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanDeriveAncestorChainResult_ParentItemId_IsNeverOmitted()
    {
        // Bug #8 fix: this field overrides the context-level WhenWritingNull
        // default to guarantee a stable wire shape under conductor's
        // strict_undefined. The resolver lint reads can_omit_when_null
        // (NOT nullability) to decide whether a default() guard is required.
        var field = Field("Polyphony.PlanDeriveAncestorChainResult", "parent_item_id");

        field["ignore_condition"]!.GetValue<string>().ShouldBe("Never");
        field["can_omit_when_null"]!.GetValue<bool>().ShouldBeFalse();
        field["nullable_annotation"]!.GetValue<string>().ShouldBe("Annotated");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Golden 2 — list of scalar
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanDeriveAncestorChainResult_AncestorChain_IsListOfScalar()
    {
        // IReadOnlyList<string> → kind=list, element_kind=scalar.
        // The lint allows `length` / integer-index walks past a list of
        // scalars but rejects deep-into-primitive references.
        var field = Field("Polyphony.PlanDeriveAncestorChainResult", "ancestor_chain");

        field["kind"]!.GetValue<string>().ShouldBe("list");
        field["element_kind"]!.GetValue<string>().ShouldBe("scalar");
        // Display shape comes from Roslyn's ToDisplayString (uses C# keyword
        // "string" rather than "System.String").
        field["element_clr_type"]!.GetValue<string>().ShouldBe("string");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Golden 3 — list of nested DTO
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanStatusResult_Items_IsListOfObject_WithElementTypeRef()
    {
        // IReadOnlyList<PlanStatusItem> → kind=list, element_kind=object,
        // element_type_ref pointing at the nested DTO (resolvable in
        // the types map).
        var field = Field("Polyphony.PlanStatusResult", "items");

        field["kind"]!.GetValue<string>().ShouldBe("list");
        field["element_kind"]!.GetValue<string>().ShouldBe("object");

        var elementRef = field["element_type_ref"]!.GetValue<string>();
        elementRef.ShouldBe("Polyphony.PlanStatusItem");
        Types[elementRef].ShouldNotBeNull(
            $"element_type_ref '{elementRef}' must resolve in the types map");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Golden 4 — per-property [JsonPropertyName] override
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanStatusSummary_PlanNa_UsesJsonPropertyNameOverride()
    {
        // Default snake_case of `PlanNa` would be `plan_na`. The
        // [JsonPropertyName("plan_n_a")] override on the property
        // preserves the slash-as-word-boundary spelling. The catalog's
        // `name` must reflect the override, not the default.
        var summary = Types["Polyphony.PlanStatusSummary"]?.AsObject();
        summary.ShouldNotBeNull();
        var fields = summary!["fields"]!.AsArray();

        var names = fields.Select(f => f!.AsObject()["name"]!.GetValue<string>()).ToList();
        names.ShouldContain("plan_n_a", "expected the [JsonPropertyName] override to win");
        names.ShouldNotContain("plan_na", "default snake_case spelling must not leak when an override exists");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Golden 5 — Dictionary<string, V> as map
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ManifestReadPlanGenerationSnapshotResult_AncestorPlanGenerations_IsScalarMap()
    {
        // Dictionary<string, int> → kind=map, key_kind=scalar,
        // value_kind=scalar. Map keys serialize as strings; the lint
        // recurses on value_kind for the value-position walk.
        var field = Field("Polyphony.ManifestReadPlanGenerationSnapshotResult", "ancestor_plan_generations");

        field["kind"]!.GetValue<string>().ShouldBe("map");
        field["key_kind"]!.GetValue<string>().ShouldBe("scalar");
        field["key_clr_type"]!.GetValue<string>().ShouldBe("string");
        field["value_kind"]!.GetValue<string>().ShouldBe("scalar");
        field["value_clr_type"]!.GetValue<string>().ShouldBe("int");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Golden 6 — chained type_ref walk through nested DTOs
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void BranchLoadTree_TypeRefChain_IsInternallyConsistent()
    {
        // ADR-described chain: branch load-tree result → work_tree → work_items → tasks.
        // The actual DTO graph names the intermediate field `issues` (not
        // `work_items`) — the chain shape is identical, the link names
        // just match the in-source DTOs (WorkTree.Issues : WorkTreeIssue,
        // WorkTreeIssue.Tasks : WorkTreeTask). Asserting the real names
        // is what the resolver lint will key on at runtime.
        var verb = Verbs["branch load-tree"]?.AsObject();
        verb.ShouldNotBeNull("verb 'branch load-tree' is missing from the catalog");
        var resultType = verb!["result_type"]!.GetValue<string>();

        // Step 1: result root → work_tree (kind=object, type_ref → WorkTree).
        var workTreeField = Field(resultType, "work_tree");
        workTreeField["kind"]!.GetValue<string>().ShouldBe("object");
        var workTreeRef = workTreeField["type_ref"]!.GetValue<string>();

        // Step 2: WorkTree → issues (kind=list, element_type_ref → WorkTreeIssue).
        var issuesField = Field(workTreeRef, "issues");
        issuesField["kind"]!.GetValue<string>().ShouldBe("list");
        issuesField["element_kind"]!.GetValue<string>().ShouldBe("object");
        var issueRef = issuesField["element_type_ref"]!.GetValue<string>();

        // Step 3: WorkTreeIssue → tasks (kind=list, element_type_ref → WorkTreeTask).
        var tasksField = Field(issueRef, "tasks");
        tasksField["kind"]!.GetValue<string>().ShouldBe("list");
        tasksField["element_kind"]!.GetValue<string>().ShouldBe("object");
        var taskRef = tasksField["element_type_ref"]!.GetValue<string>();

        // Step 4: WorkTreeTask resolves in the types map (terminal; the
        // resolver lint stops walking once it hits a leaf object whose
        // fields are all scalars — but the link must still resolve).
        Types[taskRef].ShouldNotBeNull(
            $"chain endpoint '{taskRef}' must resolve in the types map");
    }
}
