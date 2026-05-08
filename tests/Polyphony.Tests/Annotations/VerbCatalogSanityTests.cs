using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Annotations;

/// <summary>
/// Sanity checks on the verb output schema catalog: the in-memory C#
/// constant produced by <c>Polyphony.SchemaGenerator</c> and the JSON
/// artifact produced by <c>Polyphony.SchemaExporter</c>'s AfterBuild
/// step must agree byte-for-byte. Catches an exporter regression that
/// would silently let the on-disk artifact and the embedded constant
/// diverge.
/// </summary>
public sealed class VerbCatalogSanityTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, "Polyphony.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repo root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Catalog_JsonConstant_IsNonEmpty_AndParses()
    {
        var json = VerbOutputSchemaCatalog.Json;
        json.ShouldNotBeNullOrWhiteSpace();
        // ParseAsObject throws if the constant isn't valid JSON.
        var node = JsonNode.Parse(json);
        node.ShouldNotBeNull();
        node.AsObject().ShouldNotBeNull();
    }

    [Fact]
    public void Catalog_HasVersionOne_AndNonEmptyVerbsAndTypesMaps()
    {
        var root = JsonNode.Parse(VerbOutputSchemaCatalog.Json)!.AsObject();

        root["version"].ShouldNotBeNull();
        root["version"]!.GetValue<int>().ShouldBe(1);

        var verbs = root["verbs"]?.AsObject();
        verbs.ShouldNotBeNull();
        verbs!.Count.ShouldBeGreaterThan(0, "verbs map must be populated");

        var types = root["types"]?.AsObject();
        types.ShouldNotBeNull();
        types!.Count.ShouldBeGreaterThan(0, "types map must be populated");
    }

    [Fact]
    public void Catalog_HasVerbsUnderEachKnownTopLevelGroup()
    {
        // Per ADR § "Sanity test": the registry must reach every group
        // that Program.cs registers a Commands class for (excluding the
        // top-level `app.Add<T>()` ones, which carry [VerbGroup("")]).
        var root = JsonNode.Parse(VerbOutputSchemaCatalog.Json)!.AsObject();
        var verbKeys = root["verbs"]!.AsObject().Select(kv => kv.Key).ToList();

        string[] expectedGroups = ["agent", "branch", "pr", "plan", "state", "manifest", "policy", "worktree", "lock", "scope", "edges"];
        foreach (var group in expectedGroups)
        {
            verbKeys.ShouldContain(k => k.StartsWith(group + " ", StringComparison.Ordinal),
                $"Expected at least one verb under group '{group}'.");
        }
    }

    [Fact]
    public void Catalog_ArtifactFile_ExistsAtRepoRoot_AndMatchesEmbeddedJson()
    {
        var root = FindRepoRoot();
        var artifactPath = Path.Combine(root, "artifacts", "verb-output-schemas.json");
        File.Exists(artifactPath).ShouldBeTrue(
            $"artifacts/verb-output-schemas.json should be produced by the AfterBuild exporter target. " +
            $"Looked at: {artifactPath}");

        var diskJson = File.ReadAllText(artifactPath);
        diskJson.ShouldBe(VerbOutputSchemaCatalog.Json,
            "On-disk artifact and embedded VerbOutputSchemaCatalog.Json must match byte-for-byte. " +
            "If they don't, the exporter and the generator are out of sync.");
    }

    [Fact]
    public void Catalog_EveryVerbResultType_HasMatchingTypesMapEntry()
    {
        // Per ADR § "JSON shape": every verb's result_type must be
        // resolvable in the types map (no dangling references). Without
        // this, #175's resolver lint would key off a verb but find no
        // schema to walk.
        var root = JsonNode.Parse(VerbOutputSchemaCatalog.Json)!.AsObject();
        var verbs = root["verbs"]!.AsObject();
        var types = root["types"]!.AsObject();

        var dangling = new List<string>();
        foreach (var kv in verbs)
        {
            var resultType = kv.Value!.AsObject()["result_type"]!.GetValue<string>();
            if (types[resultType] is null)
            {
                dangling.Add($"verb '{kv.Key}' → result_type '{resultType}' has no entry in types map");
            }
        }
        dangling.ShouldBeEmpty(string.Join(Environment.NewLine, dangling));
    }
}
