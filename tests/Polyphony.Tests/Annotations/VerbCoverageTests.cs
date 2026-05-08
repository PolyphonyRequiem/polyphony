using System.Reflection;
using Polyphony.Annotations;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Annotations;

/// <summary>
/// Reflection-based regression net for the verb output schema registry
/// (see <c>docs/decisions/verb-output-schema-registry.md</c>).
///
/// <para>For every method bearing ConsoleAppFramework's
/// <c>[Command]</c> attribute in the Polyphony assembly, asserts:
/// <list type="bullet">
///   <item>the method carries <see cref="VerbResultAttribute"/>;</item>
///   <item>the declaring class carries <see cref="VerbGroupAttribute"/>.</item>
/// </list>
/// Catches the "someone added a new verb and forgot the annotations"
/// regression. The source generator emits <c>POLY1001</c> / <c>POLY1003</c>
/// at compile time for the same condition, but those are silenceable;
/// these tests fail the build at <c>dotnet test</c> time.</para>
/// </summary>
public sealed class VerbCoverageTests
{
    private static readonly Assembly PolyphonyAssembly = typeof(VerbGroupAttribute).Assembly;

    private static IEnumerable<(MethodInfo Method, CustomAttributeData Attr)> AllCommandMethods()
    {
        foreach (var type in PolyphonyAssembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract && type.IsSealed)
            {
                // Skip static classes and non-class types.
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var commandAttr = method.GetCustomAttributesData()
                    .FirstOrDefault(IsCommandAttribute);
                if (commandAttr is null)
                {
                    continue;
                }
                yield return (method, commandAttr);
            }
        }
    }

    private static bool IsCommandAttribute(CustomAttributeData attr)
    {
        // Match by name + namespace rather than by typeof — ConsoleAppFramework's
        // CommandAttribute is source-generated and may not be loadable by the
        // test process via direct typeof lookup. Mirrors the generator's own
        // discovery (VerbSchemaGenerator.IsCommandAttribute).
        var t = attr.AttributeType;
        if (t.Name != "CommandAttribute")
        {
            return false;
        }
        return t.Namespace == "ConsoleAppFramework" || string.IsNullOrEmpty(t.Namespace);
    }

    [Fact]
    public void AssemblyHasCommandMethods()
    {
        // Sanity: if reflection finds zero [Command] methods, the rest of
        // the suite is silently vacuous. Polyphony has ~86 verbs.
        AllCommandMethods().Count().ShouldBeGreaterThan(50,
            "Reflection found no [Command]-marked methods in the Polyphony assembly — " +
            "either the attribute moved namespaces or the assembly didn't load.");
    }

    [Fact]
    public void EveryCommandMethod_HasVerbResultAttribute()
    {
        var missing = AllCommandMethods()
            .Where(pair => pair.Method.GetCustomAttribute<VerbResultAttribute>() is null)
            .Select(pair => $"{pair.Method.DeclaringType?.FullName}.{pair.Method.Name}")
            .ToList();

        missing.ShouldBeEmpty(
            $"The following [Command]-marked methods are missing [VerbResult(typeof(...))]:{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing.Select(m => $"  - {m}")));
    }

    [Fact]
    public void EveryCommandMethod_DeclaringClassHasVerbGroupAttribute()
    {
        var missing = AllCommandMethods()
            .Select(pair => pair.Method.DeclaringType!)
            .Distinct()
            .Where(t => t.GetCustomAttribute<VerbGroupAttribute>() is null)
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        missing.ShouldBeEmpty(
            $"The following classes contain [Command] methods but lack [VerbGroup(\"...\")] " +
            $"(use [VerbGroup(\"\")] for top-level commands):{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing.Select(m => $"  - {m}")));
    }

    [Fact]
    public void EveryCommandMethod_VerbResultTypeIsRegisteredOnJsonContext()
    {
        // Catches POLY1002 at test time as well: the [VerbResult(typeof(X))]
        // type must be reachable from PolyphonyJsonContext via [JsonSerializable].
        var registeredTypes = typeof(PolyphonyJsonContext)
            .GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "System.Text.Json.Serialization.JsonSerializableAttribute"
                        && a.ConstructorArguments.Count > 0)
            .Select(a => a.ConstructorArguments[0].Value as Type)
            .Where(t => t is not null)
            .ToHashSet();

        var unregistered = AllCommandMethods()
            .Select(pair => (Method: pair.Method, Result: pair.Method.GetCustomAttribute<VerbResultAttribute>()))
            .Where(x => x.Result is not null && !registeredTypes.Contains(x.Result!.ResultType))
            .Select(x => $"{x.Method.DeclaringType?.FullName}.{x.Method.Name} → {x.Result!.ResultType.FullName}")
            .ToList();

        unregistered.ShouldBeEmpty(
            $"The following [VerbResult(typeof(X))] types are not [JsonSerializable]-registered " +
            $"on PolyphonyJsonContext (would emit POLY1002 at build):{Environment.NewLine}" +
            string.Join(Environment.NewLine, unregistered.Select(m => $"  - {m}")));
    }
}
