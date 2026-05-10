using System.Collections;
using System.Reflection;
using System.Text.Json;
using Polyphony;
using Polyphony.Annotations;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Move #2 runtime regression: invoke EVERY <c>[Command]</c> method that
/// declares one or more sentinel-default required arguments using all
/// sentinel values. The verb body must short-circuit via
/// <see cref="RequiredInput.HaltIfMissing"/> and emit the routing-style
/// envelope on stdout.
///
/// <para>Reflection-driven so newly-added verbs are auto-covered. Constructor
/// parameters are passed as <c>null</c> — the halt check fires before any
/// dependency is dereferenced, so DI is irrelevant.</para>
///
/// <para>Verbs whose required params don't use the sentinel pattern (e.g.
/// only optional flags) are skipped by the discovery filter — they have
/// nothing to halt on.</para>
/// </summary>
public sealed class VerbHaltOnMissingTheory(ITestOutputHelper output)
{
    private static readonly Assembly PolyphonyAssembly = typeof(VerbGroupAttribute).Assembly;

    /// <summary>
    /// Methods owned by Move #3 (commit-and-push migration). Move #2 leaves
    /// these untouched per agreed contract — they still validate inputs via
    /// their own pre-existing envelope shape rather than the routing-style
    /// <see cref="RequiredInputErrorResult"/>. Remove from this set when
    /// Move #3 migrates them.
    /// </summary>
    private static readonly HashSet<string> Move3ConflictZone = new(StringComparer.Ordinal)
    {
        "Polyphony.Commands.ManifestCommands.CommitAndPush",
        "Polyphony.Commands.PlanCommands.CommitAndPush",
    };

    /// <summary>
    /// Stage 4 (Rev 4.2 manifest path resolution): these read-only manifest
    /// verbs accept <c>--path</c> and <c>--root-id</c> as <em>both optional</em>
    /// because they support a Stage-8-transitional legacy fallback path
    /// (<c>.polyphony/run.yaml</c>). The <c>path = ""</c> default is a
    /// "derive-from-root-id-or-fall-back" sentinel, NOT a Move #2
    /// "missing required" sentinel — there is genuinely nothing to halt on
    /// when both flags are omitted. Stage 8 will eliminate the legacy
    /// fallback and require <c>--root-id</c> at every caller; once that
    /// lands, these verbs can drop the empty-string default and these
    /// exclusions can be removed.
    /// </summary>
    private static readonly HashSet<string> Stage4OptionalSentinelZone = new(StringComparer.Ordinal)
    {
        "Polyphony.Commands.ManifestCommands.Read",
        "Polyphony.Commands.ManifestCommands.TopologyHash",
    };

    public static IEnumerable<object[]> SentinelVerbs()
    {
        foreach (var type in PolyphonyAssembly.GetTypes())
        {
            if (!type.IsClass) continue;
            if (type.IsAbstract && type.IsSealed) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!method.GetCustomAttributesData().Any(IsCommandAttribute)) continue;
                if (!HasSentinelRequiredParam(method)) continue;
                var fqn = $"{type.FullName}.{method.Name}";
                if (Move3ConflictZone.Contains(fqn)) continue;
                if (Stage4OptionalSentinelZone.Contains(fqn)) continue;
                yield return new object[] { fqn };
            }
        }
    }

    [Theory]
    [MemberData(nameof(SentinelVerbs))]
    public async Task SentinelVerb_AllArgsMissing_EmitsRoutingEnvelope(string fqn)
    {
        var (type, method) = ResolveMethod(fqn);

        var instance = method.IsStatic ? null : InstantiateWithNullDeps(type);
        var args = method.GetParameters().Select(BuildSentinelArg).ToArray();

        var (exitCode, stdout) = await CaptureAsync(async () =>
        {
            var raw = method.Invoke(instance, args);
            return raw switch
            {
                Task<int> t => await t.ConfigureAwait(false),
                int i => i,
                _ => throw new InvalidOperationException(
                    $"{fqn} returned {raw?.GetType().FullName ?? "null"}; expected int or Task<int>."),
            };
        });

        exitCode.ShouldBe(ExitCodes.RoutingFailure,
            $"{fqn} did not return RoutingFailure on missing args. Stdout was: <{stdout}>");

        stdout.ShouldNotBeNullOrWhiteSpace($"{fqn} emitted no envelope on missing args.");

        RequiredInputErrorResult? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(
                stdout, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        }
        catch (JsonException ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"{fqn} stdout was not parseable as RequiredInputErrorResult: {ex.Message}\nStdout: <{stdout}>");
        }

        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error", $"{fqn} envelope.action wrong. Stdout: <{stdout}>");
        envelope.Verb.ShouldNotBeNullOrEmpty($"{fqn} envelope.verb empty. Stdout: <{stdout}>");
        envelope.MissingArgs.ShouldNotBeEmpty($"{fqn} envelope.missing_args empty. Stdout: <{stdout}>");

        output.WriteLine($"{fqn} → verb='{envelope.Verb}' missing=[{string.Join(",", envelope.MissingArgs)}]");
    }

    private static (Type Type, MethodInfo Method) ResolveMethod(string fqn)
    {
        var dot = fqn.LastIndexOf('.');
        var typeName = fqn[..dot];
        var methodName = fqn[(dot + 1)..];
        var type = PolyphonyAssembly.GetType(typeName)
            ?? throw new InvalidOperationException($"Type not found: {typeName}");
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == methodName && m.GetCustomAttributesData().Any(IsCommandAttribute))
            ?? throw new InvalidOperationException($"[Command] method not found: {fqn}");
        return (type, method);
    }

    private static object? InstantiateWithNullDeps(Type type)
    {
        var ctors = type.GetConstructors();
        // Pick the ctor with the most parameters (primary ctor in C# 12 partials lands first).
        var ctor = ctors.OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => DefaultForType(p.ParameterType)).ToArray();
        return ctor.Invoke(args);
    }

    private static object? DefaultForType(Type t)
    {
        if (t.IsValueType) return Activator.CreateInstance(t);
        return null; // null! for reference deps — halt path never touches them.
    }

    private static object? BuildSentinelArg(ParameterInfo p)
    {
        // CancellationToken: pass default.
        if (p.ParameterType == typeof(CancellationToken)) return CancellationToken.None;
        // Use the parameter's own declared default — that's the sentinel by design.
        if (p.HasDefaultValue) return p.DefaultValue;
        return DefaultForType(p.ParameterType);
    }

    private static bool HasSentinelRequiredParam(MethodInfo method)
    {
        foreach (var p in method.GetParameters())
        {
            if (!p.HasDefaultValue) continue;
            if (p.ParameterType == typeof(int) && p.DefaultValue is int i && i == RequiredInput.MissingInt)
                return true;
            if (p.ParameterType == typeof(string) && p.DefaultValue is string s && s.Length == 0)
                return true;
        }
        return false;
    }

    private static bool IsCommandAttribute(CustomAttributeData attr)
    {
        if (attr.AttributeType.Name != "CommandAttribute") return false;
        return attr.AttributeType.Namespace == "ConsoleAppFramework"
            || string.IsNullOrEmpty(attr.AttributeType.Namespace);
    }

    private static async Task<(int ExitCode, string Output)> CaptureAsync(Func<Task<int>> action)
    {
        await ConsoleTestLock.AsyncLock.WaitAsync();
        try
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var exitCode = await action();
                return (exitCode, writer.ToString().Trim());
            }
            finally { Console.SetOut(original); }
        }
        finally { ConsoleTestLock.AsyncLock.Release(); }
    }
}
