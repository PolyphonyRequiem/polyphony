using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// SHA-based freshness gate: fails CI when
    /// <c>artifacts/verb-output-schemas.json</c> drifts from what the
    /// current verb annotations would generate.
    ///
    /// <para>
    /// Both sides are canonicalized (object keys sorted, consistent
    /// indentation) before hashing so that whitespace-only reformats
    /// don't trigger false positives.
    /// </para>
    ///
    /// <para>
    /// NOTE: this test is RED until Mozart's <c>[VerbResult]</c> backfill
    /// PR (<c>squad/527-verbresult-attrs</c>) lands and regenerates the
    /// artifact. That is expected and intentional.
    /// </para>
    ///
    /// <para>
    /// If this test fails, run:
    /// <code>dotnet build src/Polyphony.SchemaExporter -c Release</code>
    /// </para>
    /// </summary>
    [Fact]
    public void VerbOutputSchemas_ArtifactIsFresh()
    {
        var repoRoot = FindRepoRoot();
        var artifactPath = Path.Combine(repoRoot, "artifacts", "verb-output-schemas.json");

        File.Exists(artifactPath).ShouldBeTrue(
            $"artifacts/verb-output-schemas.json is missing. " +
            $"Run: dotnet build src/Polyphony.SchemaExporter -c Release" +
            $"{Environment.NewLine}Looked at: {artifactPath}");

        var embeddedCanon = CanonicalizeJson(VerbOutputSchemaCatalog.Json);
        var diskCanon = CanonicalizeJson(File.ReadAllText(artifactPath));

        var embeddedSha = Sha256Hex(embeddedCanon);
        var diskSha = Sha256Hex(diskCanon);

        diskSha.ShouldBe(embeddedSha,
            $"artifacts/verb-output-schemas.json is stale (SHA mismatch).{Environment.NewLine}" +
            $"  On-disk SHA : {diskSha}{Environment.NewLine}" +
            $"  Expected SHA: {embeddedSha}{Environment.NewLine}" +
            $"Run: dotnet build src/Polyphony.SchemaExporter -c Release");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers for freshness check
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse <paramref name="json"/>, sort all object keys recursively,
    /// and re-serialize with consistent indentation. Two JSON documents
    /// that are semantically equivalent will produce the same canonical
    /// string regardless of original key order or whitespace.
    /// </summary>
    private static string CanonicalizeJson(string json)
    {
        var node = JsonNode.Parse(json)!;
        return SortNode(node).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode SortNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var kv in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                sorted.Add(kv.Key, kv.Value is not null ? SortNode(kv.Value.DeepClone()) : null);
            }
            return sorted;
        }

        if (node is JsonArray arr)
        {
            var sortedArr = new JsonArray();
            foreach (var item in arr)
            {
                sortedArr.Add(item is not null ? SortNode(item.DeepClone()) : null);
            }
            return sortedArr;
        }

        return node.DeepClone();
    }

    private static string Sha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
