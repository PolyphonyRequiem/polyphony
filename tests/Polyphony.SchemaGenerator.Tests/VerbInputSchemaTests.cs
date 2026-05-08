using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Polyphony.SchemaGenerator.Tests;

/// <summary>
/// Pins the <c>inputs</c> column emitted by <see cref="VerbSchemaGenerator"/>
/// for each verb. Mirrors ConsoleAppFramework's PascalCase→kebab-case CLI
/// flag mapping so the workflow author lint can cross-check verb call sites
/// against the authoritative C# signature.
/// </summary>
public sealed class VerbInputSchemaTests
{
    private const string Header = """
        using System.Text.Json.Serialization;
        using System.Threading;
        using Polyphony.Annotations;
        using ConsoleAppFramework;
        namespace Fixture;

        public sealed record R { public required int X { get; init; } }

        [JsonSerializable(typeof(R))]
        public partial class Ctx : JsonSerializerContext { }
        """;

    private static JsonArray InputsFor(string verbBody, string verbName = "v")
    {
        var src = Header + $$"""

            [VerbGroup("demo")]
            public class Cmd
            {
                [Command("{{verbName}}")]
                [VerbResult(typeof(R))]
                public int V({{verbBody}}) => 0;
            }
            """;
        var run = GeneratorTestHarness.Run(src);
        var json = JsonNode.Parse(GeneratorTestHarness.GetCatalogJson(run))!.AsObject();
        var verb = json["verbs"]![$"demo {verbName}"]!.AsObject();
        return verb["inputs"]!.AsArray();
    }

    [Fact]
    public void NoParameters_EmitsEmptyInputsArray()
    {
        var inputs = InputsFor("");
        inputs.Count.ShouldBe(0);
    }

    [Fact]
    public void RequiredInt_EmittedWithRequiredTrueAndNoDefault()
    {
        var inputs = InputsFor("int workItem");
        inputs.Count.ShouldBe(1);
        var p = inputs[0]!.AsObject();
        p["name"]!.GetValue<string>().ShouldBe("work-item");
        p["clr_type"]!.GetValue<string>().ShouldBe("int");
        p["required"]!.GetValue<bool>().ShouldBeTrue();
        p.ContainsKey("default").ShouldBeFalse();
    }

    [Fact]
    public void OptionalInt_EmittedWithRequiredFalseAndNumericDefault()
    {
        var inputs = InputsFor("int depth = 3");
        var p = inputs[0]!.AsObject();
        p["required"]!.GetValue<bool>().ShouldBeFalse();
        p["default"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void OptionalString_EmittedWithQuotedDefault()
    {
        var inputs = InputsFor("string config = \".conductor/process-config.yaml\"");
        var p = inputs[0]!.AsObject();
        p["required"]!.GetValue<bool>().ShouldBeFalse();
        p["default"]!.GetValue<string>().ShouldBe(".conductor/process-config.yaml");
    }

    [Fact]
    public void OptionalBool_EmittedWithBooleanDefault()
    {
        var inputs = InputsFor("bool includeMetadata = false");
        var p = inputs[0]!.AsObject();
        p["name"]!.GetValue<string>().ShouldBe("include-metadata");
        p["clr_type"]!.GetValue<string>().ShouldBe("bool");
        p["required"]!.GetValue<bool>().ShouldBeFalse();
        p["default"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void NullableStringDefaultingToNull_EmitsNullDefault()
    {
        var inputs = InputsFor("string? note = null");
        var p = inputs[0]!.AsObject();
        p["clr_type"]!.GetValue<string>().ShouldBe("string?");
        p["required"]!.GetValue<bool>().ShouldBeFalse();
        // The `default` key must be present (omitting it would imply
        // "no default", which would be wrong) but its value is JSON null.
        p.ContainsKey("default").ShouldBeTrue("default key must be emitted even when the value is null");
        p["default"].ShouldBeNull("default value should serialize as JSON null");
    }

    [Fact]
    public void CancellationToken_IsExcludedFromInputs()
    {
        // CT may be the only param or sit at the end of a real signature;
        // either way it should never appear as a CLI flag.
        var inputs = InputsFor("CancellationToken ct = default");
        inputs.Count.ShouldBe(0);

        var withReal = InputsFor("int workItem, CancellationToken ct = default", "w");
        withReal.Count.ShouldBe(1);
        withReal[0]!["name"]!.GetValue<string>().ShouldBe("work-item");
    }

    [Fact]
    public void EscapedKeywordParameter_DropsAtSigilInKebabName()
    {
        // `string @event` — the C# escape sigil is not part of the symbol
        // Name, so the emitted CLI flag is plain `--event`.
        var inputs = InputsFor("string @event");
        inputs[0]!["name"]!.GetValue<string>().ShouldBe("event");
    }

    [Fact]
    public void PascalCase_ConvertsToKebabCaseAtWordBoundaries()
    {
        var inputs = InputsFor("int rootId, int prNumber, string repositoryId");
        inputs[0]!["name"]!.GetValue<string>().ShouldBe("root-id");
        inputs[1]!["name"]!.GetValue<string>().ShouldBe("pr-number");
        inputs[2]!["name"]!.GetValue<string>().ShouldBe("repository-id");
    }

    [Fact]
    public void OrderingPreservesParameterDeclarationOrder()
    {
        var inputs = InputsFor("int a, int b, int c");
        inputs[0]!["name"]!.GetValue<string>().ShouldBe("a");
        inputs[1]!["name"]!.GetValue<string>().ShouldBe("b");
        inputs[2]!["name"]!.GetValue<string>().ShouldBe("c");
    }

    [Fact]
    public void RequiredAndOptionalMix_PreservesShape()
    {
        var inputs = InputsFor("int workItem, int maxAncestorWalk = 32");
        inputs.Count.ShouldBe(2);
        inputs[0]!["required"]!.GetValue<bool>().ShouldBeTrue();
        inputs[1]!["required"]!.GetValue<bool>().ShouldBeFalse();
        inputs[1]!["default"]!.GetValue<int>().ShouldBe(32);
    }
}
