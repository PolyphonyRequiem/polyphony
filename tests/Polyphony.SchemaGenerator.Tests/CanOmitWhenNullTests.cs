using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Polyphony.SchemaGenerator.Tests;

/// <summary>
/// Pins the <c>can_omit_when_null</c> column emitted by
/// <see cref="VerbSchemaGenerator"/> for each combination of
/// <c>[JsonIgnore(Condition = …)]</c> and C# nullability annotation.
///
/// <para>Pre-fix history: the generator returned <c>canOmit = ignore != "Never"</c>,
/// which made every field omittable because the global serializer policy is
/// <c>WhenWritingNull</c>. That produced 200+ false-positive JINJA002
/// warnings against fields the C# nullable type system already proves
/// non-null (e.g. <c>required string State</c>). The new logic respects
/// nullability — a field is only omittable when its CLR type can actually
/// hold null/default at runtime.</para>
///
/// <para>Each test compiles a tiny inline DTO, runs the generator, parses
/// the emitted catalog JSON, and asserts <c>can_omit_when_null</c>
/// directly. Cases come from the rubber-duck table for the fix.</para>
/// </summary>
public sealed class CanOmitWhenNullTests
{
    private const string Header = """
        using System.Text.Json.Serialization;
        using Polyphony.Annotations;
        using ConsoleAppFramework;
        namespace Fixture;
        """;

    private const string ContextSuffix = """

        [JsonSerializable(typeof(R))]
        public partial class Ctx : JsonSerializerContext { }

        [VerbGroup("demo")]
        public class Cmd
        {
            [Command("v")]
            [VerbResult(typeof(R))]
            public int V() => 0;
        }
        """;

    private static JsonObject FieldFromGenerator(string recordBody)
    {
        var src = Header + "public sealed record R { " + recordBody + " }" + ContextSuffix;
        var run = GeneratorTestHarness.Run(src);
        var json = JsonNode.Parse(GeneratorTestHarness.GetCatalogJson(run))!.AsObject();
        var rType = json["types"]!["Fixture.R"]!.AsObject();
        return rType["fields"]!.AsArray()[0]!.AsObject();
    }

    [Fact]
    public void RequiredString_DefaultIgnore_IsNotOmittable()
    {
        // `required string` under <Nullable>enable</Nullable> is
        // compiler-guaranteed non-null at construction. The global
        // WhenWritingNull policy can never trigger omission in practice,
        // so the lint should treat the field as always-present.
        var f = FieldFromGenerator("public required string Foo { get; init; }");

        f["nullable_annotation"]!.GetValue<string>().ShouldBe("NotAnnotated");
        f["ignore_condition"]!.GetValue<string>().ShouldBe("WhenWritingNull");
        f["can_omit_when_null"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void NonNullableString_WithDefaultInitializer_IsNotOmittable()
    {
        // `string Foo { get; init; } = ""` — same compiler guarantee:
        // not nullable, so WhenWritingNull cannot legitimately fire.
        var f = FieldFromGenerator("public string Foo { get; init; } = \"\";");

        f["nullable_annotation"]!.GetValue<string>().ShouldBe("NotAnnotated");
        f["can_omit_when_null"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void NullableString_DefaultIgnore_IsOmittable()
    {
        // `string?` genuinely can hold null → JSON can legitimately
        // omit the field → workflow author needs to guard.
        var f = FieldFromGenerator("public string? Foo { get; init; }");

        f["nullable_annotation"]!.GetValue<string>().ShouldBe("Annotated");
        f["can_omit_when_null"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void NonNullableInt_DefaultIgnore_IsNotOmittable()
    {
        // Value types under WhenWritingNull never serialize as null,
        // so the field is always present in the JSON envelope.
        var f = FieldFromGenerator("public required int Foo { get; init; }");

        f["can_omit_when_null"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void NullableInt_DefaultIgnore_IsOmittable()
    {
        // `int?` is `Nullable<int>` — has a real null state.
        var f = FieldFromGenerator("public int? Foo { get; init; }");

        f["nullable_annotation"]!.GetValue<string>().ShouldBe("Annotated");
        f["can_omit_when_null"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void NullableString_WithJsonIgnoreNever_IsNotOmittable()
    {
        // Per-property `[JsonIgnore(Condition = Never)]` overrides the
        // global WhenWritingNull policy and pins the field to always
        // serialize, even when null. This is the "stable wire shape"
        // workaround from bug #8 that the existing golden test pins.
        var f = FieldFromGenerator(
            "[JsonIgnore(Condition = JsonIgnoreCondition.Never)]\n" +
            "public string? Foo { get; init; }");

        f["ignore_condition"]!.GetValue<string>().ShouldBe("Never");
        f["can_omit_when_null"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Bool_WithJsonIgnoreWhenWritingDefault_IsOmittable()
    {
        // WhenWritingDefault on a value type omits the default value
        // (`false` for bool). The lint correctly treats this as
        // potentially absent from the envelope.
        var f = FieldFromGenerator(
            "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]\n" +
            "public bool Foo { get; init; }");

        f["ignore_condition"]!.GetValue<string>().ShouldBe("WhenWritingDefault");
        f["can_omit_when_null"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void RequiredString_WithJsonIgnoreWhenWritingDefault_IsNotOmittable()
    {
        // `default(string)` is null, but the type contract excludes
        // null — so even with WhenWritingDefault the field cannot
        // legitimately be omitted. (If a producer assigns null!, that's
        // a CLI bug, not a lint surface to defend against.)
        var f = FieldFromGenerator(
            "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]\n" +
            "public required string Foo { get; init; }");

        f["ignore_condition"]!.GetValue<string>().ShouldBe("WhenWritingDefault");
        f["can_omit_when_null"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void NullableString_WithJsonIgnoreWhenWritingDefault_IsOmittable()
    {
        // `default(string?)` is null and the type contract permits it.
        var f = FieldFromGenerator(
            "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]\n" +
            "public string? Foo { get; init; }");

        f["can_omit_when_null"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void NonNullableList_DefaultIgnore_IsNotOmittable()
    {
        // `IReadOnlyList<string>` is a NotAnnotated reference type;
        // the C# nullable system says it's non-null. WhenWritingNull
        // can't trigger omission.
        var f = FieldFromGenerator(
            "public required System.Collections.Generic.IReadOnlyList<string> Foo { get; init; }");

        f["can_omit_when_null"]!.GetValue<bool>().ShouldBeFalse();
    }
}
