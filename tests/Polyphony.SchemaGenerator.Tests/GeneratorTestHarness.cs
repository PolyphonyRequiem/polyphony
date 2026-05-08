using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Polyphony.SchemaGenerator;

namespace Polyphony.SchemaGenerator.Tests;

/// <summary>
/// Hosts <see cref="VerbSchemaGenerator"/> against an inline fixture
/// compilation. Each test supplies one or more snippets of C# source
/// representing a fake "polyphony" assembly (with the necessary
/// <c>[VerbGroup]</c>, <c>[VerbResult]</c>, <c>[Command]</c>, and
/// <c>[JsonSerializable]</c> attribute stubs); the harness runs the
/// generator and returns <see cref="GeneratorDriverRunResult"/> for
/// diagnostic and emitted-source assertions.
///
/// <para>Why inline stubs rather than referencing the real Polyphony
/// project: the generator's discovery is by attribute *name* +
/// namespace (mirroring how it tolerates ConsoleAppFramework's
/// generated-attribute surface in production), so a hand-rolled stub
/// in the same namespace exercises the same code path without dragging
/// in 86 verbs of fixture setup.</para>
/// </summary>
internal static class GeneratorTestHarness
{
    /// <summary>
    /// Default attribute stubs the generator looks up by metadata name.
    /// The shapes here mirror the real attributes in
    /// <c>src/Polyphony/Annotations/</c> and ConsoleAppFramework's
    /// <c>CommandAttribute</c>; the generator never reads property
    /// values via reflection, only via Roslyn symbol APIs, so the
    /// stub doesn't need to be source-generated.
    /// </summary>
    public const string AttributeStubs = """
        using System;

        namespace Polyphony.Annotations
        {
            [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
            public sealed class VerbGroupAttribute : Attribute
            {
                public VerbGroupAttribute(string name) { Name = name; }
                public string Name { get; }
            }

            [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
            public sealed class VerbResultAttribute : Attribute
            {
                public VerbResultAttribute(Type resultType) { ResultType = resultType; }
                public Type ResultType { get; }
            }
        }

        namespace ConsoleAppFramework
        {
            [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
            public sealed class CommandAttribute : Attribute
            {
                public CommandAttribute(string name) { Name = name; }
                public string Name { get; }
            }
        }
        """;

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(BuildReferences);

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        // Pull every assembly the test process can see (TPA contains the
        // full netcoreapp shared framework + STJ + everything we need for
        // the fake compilation to bind System.Text.Json.Serialization,
        // INotifyPropertyChanged-style basics, generic collections, etc.).
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrEmpty(tpa))
        {
            throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES not set on AppContext.");
        }
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                builder.Add(MetadataReference.CreateFromFile(path));
            }
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// Compile the supplied user source plus the default attribute stubs,
    /// run the generator, and return the result. Compilation errors in
    /// the user source are surfaced via the run result's diagnostics
    /// list; tests that need a clean compilation should call
    /// <see cref="AssertNoCompileErrors"/> on the post-generator
    /// compilation themselves.
    /// </summary>
    public static GeneratorDriverRunResult Run(params string[] userSources)
    {
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(AttributeStubs, path: "AttributeStubs.cs"),
        };
        for (var i = 0; i < userSources.Length; i++)
        {
            trees.Add(CSharpSyntaxTree.ParseText(userSources[i], path: $"User{i}.cs"));
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: "PolyphonyFixture",
            syntaxTrees: trees,
            references: References.Value,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new VerbSchemaGenerator())
            .RunGenerators(compilation);

        return driver.GetRunResult();
    }

    /// <summary>
    /// Returns the generated source emitted by the verb-schema
    /// generator — typically the only file, named
    /// <c>VerbOutputSchemaCatalog.g.cs</c>. Throws if the generator
    /// emitted nothing (most tests want this behavior).
    /// </summary>
    public static string GetCatalogSource(GeneratorDriverRunResult result)
    {
        var generatorResult = result.Results.Single();
        var generated = generatorResult.GeneratedSources.Single(
            s => s.HintName == "VerbOutputSchemaCatalog.g.cs");
        return generated.SourceText.ToString();
    }

    /// <summary>
    /// Pull just the JSON literal out of the emitted catalog source.
    /// The generator emits a single <c>public const string Json = "..."</c>
    /// inside <c>VerbOutputSchemaCatalog</c>; this helper finds and
    /// returns the literal contents.
    /// </summary>
    public static string GetCatalogJson(GeneratorDriverRunResult result)
    {
        var src = GetCatalogSource(result);
        // The emitted shape uses a triple-quoted raw-string literal:
        //     public const string Json = """{"version":1, ...}""";
        // Find the first `"""` after `Json =` and the matching closing.
        const string marker = "Json = \"\"\"";
        var start = src.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("Could not locate Json literal in emitted catalog source:\n" + src);
        }
        start += marker.Length;
        var end = src.IndexOf("\"\"\"", start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException("Could not locate Json literal terminator in emitted catalog source.");
        }
        return src[start..end];
    }
}
