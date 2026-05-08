using System.Reflection;
using Polyphony.Annotations;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Annotations;

/// <summary>
/// Move #2 lint: every <c>[Command]</c> method's parameters must be optional
/// (have a default value), so that ConsoleAppFramework v5 NEVER rejects
/// dispatch with its built-in stderr-noise + exit-1 path. Required-ness is
/// re-asserted INSIDE the verb body via <see cref="RequiredInput.HaltIfMissing"/>
/// which emits a parseable routing envelope on stdout.
///
/// <para>If this test fails for a new verb, change the parameter to use a
/// sentinel default and add a <c>HaltIfMissing</c> early-return:
/// <code>
/// public Task&lt;int&gt; Foo(
///     int rootId = RequiredInput.MissingInt,
///     string name = "",
///     CancellationToken ct = default)
/// {
///     if (RequiredInput.HaltIfMissing("group foo",
///         ("--root-id", rootId == RequiredInput.MissingInt),
///         ("--name", string.IsNullOrEmpty(name))) is { } halt)
///         return Task.FromResult(halt);
///     // ...
/// }
/// </code>
/// </para>
/// </summary>
public sealed class VerbRequiredInputContractTests
{
    private static readonly Assembly PolyphonyAssembly = typeof(VerbGroupAttribute).Assembly;

    private static IEnumerable<MethodInfo> AllCommandMethods()
    {
        foreach (var type in PolyphonyAssembly.GetTypes())
        {
            if (!type.IsClass) continue;
            if (type.IsAbstract && type.IsSealed) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttributesData().Any(IsCommandAttribute))
                    yield return method;
            }
        }
    }

    private static bool IsCommandAttribute(CustomAttributeData attr)
    {
        if (attr.AttributeType.Name != "CommandAttribute") return false;
        return attr.AttributeType.Namespace == "ConsoleAppFramework"
            || string.IsNullOrEmpty(attr.AttributeType.Namespace);
    }

    /// <summary>
    /// Methods owned by Move #3 (commit-and-push migration). Move #2 leaves
    /// these untouched per agreed contract; remove from this set when Move #3
    /// migrates them to the sentinel-default + HaltIfMissing pattern.
    /// </summary>
    private static readonly HashSet<string> Move3ConflictZone = new(StringComparer.Ordinal)
    {
        "Polyphony.Commands.ManifestCommands.CommitAndPush",
        "Polyphony.Commands.PlanCommands.CommitAndPush",
    };

    [Fact]
    public void EveryCommandParameter_HasDefaultValue()
    {
        var offenders = new List<string>();

        foreach (var method in AllCommandMethods())
        {
            var fqn = $"{method.DeclaringType?.FullName}.{method.Name}";
            if (Move3ConflictZone.Contains(fqn)) continue;

            foreach (var param in method.GetParameters())
            {
                if (param.HasDefaultValue) continue;
                // CancellationToken is supplied by ConsoleAppFramework itself
                // and never appears on the operator's command line — skip it.
                if (param.ParameterType == typeof(CancellationToken)) continue;

                offenders.Add($"{fqn}({param.Name} : {param.ParameterType.Name})");
            }
        }

        offenders.ShouldBeEmpty(
            "Every [Command] parameter must have a default value so CAF v5 never rejects dispatch. " +
            "Use sentinel defaults (RequiredInput.MissingInt for ints, \"\" for strings) and re-assert " +
            "required-ness in the verb body via RequiredInput.HaltIfMissing. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }
}
