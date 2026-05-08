using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit;

namespace Polyphony.SchemaGenerator.Tests;

/// <summary>
/// One test per Roslyn diagnostic emitted by
/// <see cref="VerbSchemaGenerator"/>: covers every code in the
/// <c>POLY1001</c>–<c>POLY1006</c> range plus the happy path that
/// must produce zero diagnostics and a populated catalog entry.
/// </summary>
public sealed class GeneratorDiagnosticsTests
{
    private const string MinimalContext = """
        using System.Text.Json.Serialization;
        namespace Fixture
        {
            public sealed record FixtureResult { public int X { get; init; } }

            [JsonSerializable(typeof(FixtureResult))]
            public partial class FixtureJsonContext : JsonSerializerContext { }
        }
        """;

    // ─────────────────────────────────────────────────────────────────────
    // POLY1001 — [Command] without [VerbResult]
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void MissingVerbResult_EmitsPoly1001()
    {
        const string user = """
            using ConsoleAppFramework;
            using Polyphony.Annotations;
            namespace Fixture
            {
                [VerbGroup("demo")]
                public class DemoCommands
                {
                    [Command("noop")]
                    public int Noop() => 0;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(user, MinimalContext);

        result.Diagnostics.ShouldContain(d => d.Id == "POLY1001"
            && d.Severity == DiagnosticSeverity.Error
            && d.GetMessage().Contains("Noop"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // POLY1002 — [VerbResult(typeof(X))] where X isn't on the JsonSerializerContext
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResultTypeNotInJsonContext_EmitsPoly1002()
    {
        // FixtureJsonContext registers FixtureResult only; the verb points
        // at UnregisteredResult, which the generator must flag.
        const string user = """
            using ConsoleAppFramework;
            using Polyphony.Annotations;
            namespace Fixture
            {
                public sealed record UnregisteredResult { public int Y { get; init; } }

                [VerbGroup("demo")]
                public class DemoCommands
                {
                    [Command("orphan")]
                    [VerbResult(typeof(UnregisteredResult))]
                    public int Orphan() => 0;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(user, MinimalContext);

        result.Diagnostics.ShouldContain(d => d.Id == "POLY1002"
            && d.Severity == DiagnosticSeverity.Error
            && d.GetMessage().Contains("UnregisteredResult"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // POLY1003 — class with [Command] methods but no [VerbGroup]
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void MissingVerbGroup_EmitsPoly1003()
    {
        const string user = """
            using ConsoleAppFramework;
            using Polyphony.Annotations;
            namespace Fixture
            {
                public class DemoCommands
                {
                    [Command("noop")]
                    [VerbResult(typeof(FixtureResult))]
                    public int Noop() => 0;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(user, MinimalContext);

        result.Diagnostics.ShouldContain(d => d.Id == "POLY1003"
            && d.Severity == DiagnosticSeverity.Error
            && d.GetMessage().Contains("DemoCommands"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // POLY1004 — partial-class with conflicting [VerbGroup] declarations
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConflictingVerbGroup_EmitsPoly1004()
    {
        // Two partial declarations of the same class with different
        // [VerbGroup] names must trigger POLY1004.
        const string user = """
            using ConsoleAppFramework;
            using Polyphony.Annotations;
            namespace Fixture
            {
                [VerbGroup("a")]
                public partial class DemoCommands
                {
                    [Command("noop")]
                    [VerbResult(typeof(FixtureResult))]
                    public int Noop() => 0;
                }

                [VerbGroup("b")]
                public partial class DemoCommands
                {
                }
            }
            """;

        var result = GeneratorTestHarness.Run(user, MinimalContext);

        result.Diagnostics.ShouldContain(d => d.Id == "POLY1004"
            && d.Severity == DiagnosticSeverity.Error);
    }

    // ─────────────────────────────────────────────────────────────────────
    // POLY1005 — [Command] with multiple aliases
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AliasCommand_EmitsPoly1005_AndKeysOnFirstSegment()
    {
        const string user = """
            using ConsoleAppFramework;
            using Polyphony.Annotations;
            namespace Fixture
            {
                [VerbGroup("demo")]
                public class DemoCommands
                {
                    [Command("foo|bar|baz")]
                    [VerbResult(typeof(FixtureResult))]
                    public int Foo() => 0;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(user, MinimalContext);

        result.Diagnostics.ShouldContain(d => d.Id == "POLY1005"
            && d.Severity == DiagnosticSeverity.Warning
            && d.GetMessage().Contains("foo"));

        // The verb still keys on the first alias — confirms the warning
        // doesn't suppress emission.
        var json = GeneratorTestHarness.GetCatalogJson(result);
        json.ShouldContain("\"demo foo\"");
    }

    // ─────────────────────────────────────────────────────────────────────
    // POLY1006 — empty [Command] name
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyCommandName_EmitsPoly1006()
    {
        const string user = """
            using ConsoleAppFramework;
            using Polyphony.Annotations;
            namespace Fixture
            {
                [VerbGroup("demo")]
                public class DemoCommands
                {
                    [Command("")]
                    [VerbResult(typeof(FixtureResult))]
                    public int Empty() => 0;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(user, MinimalContext);

        result.Diagnostics.ShouldContain(d => d.Id == "POLY1006"
            && d.Severity == DiagnosticSeverity.Error
            && d.GetMessage().Contains("Empty"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Happy path — well-annotated class produces a populated catalog with
    // no diagnostics
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void WellAnnotatedClass_NoDiagnostics_AndCatalogContainsEntry()
    {
        const string user = """
            using ConsoleAppFramework;
            using Polyphony.Annotations;
            namespace Fixture
            {
                [VerbGroup("demo")]
                public class DemoCommands
                {
                    [Command("ok")]
                    [VerbResult(typeof(FixtureResult))]
                    public int Ok() => 0;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(user, MinimalContext);

        // Filter to only this generator's diagnostics — a fixture
        // compilation-warning should not fail the test, but POLY1xxx
        // codes must all be absent.
        var poly = result.Diagnostics.Where(d => d.Id.StartsWith("POLY", StringComparison.Ordinal)).ToList();
        poly.ShouldBeEmpty(
            "Happy-path fixture should produce zero POLYxxxx diagnostics. Got: " +
            string.Join(", ", poly.Select(d => $"{d.Id} {d.GetMessage()}")));

        var json = GeneratorTestHarness.GetCatalogJson(result);
        json.ShouldContain("\"demo ok\"");
        json.ShouldContain("\"Fixture.FixtureResult\"");
        // The DTO walk should pick up the public `X` property as a scalar field.
        json.ShouldContain("\"x\"");
    }

    // ─────────────────────────────────────────────────────────────────────
    // Happy path — top-level class (empty group) keys the verb on the bare
    // command name
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TopLevelClass_EmptyGroup_KeysVerbOnCommandNameOnly()
    {
        // POLY1003's documented exception: top-level commands carry
        // [VerbGroup("")] and the verb path is just the command name.
        const string user = """
            using ConsoleAppFramework;
            using Polyphony.Annotations;
            namespace Fixture
            {
                [VerbGroup("")]
                public class HealthCommand
                {
                    [Command("health")]
                    [VerbResult(typeof(FixtureResult))]
                    public int Health() => 0;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(user, MinimalContext);
        result.Diagnostics.Where(d => d.Id.StartsWith("POLY", StringComparison.Ordinal)).ShouldBeEmpty();

        var json = GeneratorTestHarness.GetCatalogJson(result);
        json.ShouldContain("\"health\":{");
        // Specifically, no leading-space key like `" health"` or `"<group> health"`.
        json.ShouldNotContain("\" health\"");
    }
}
