using System.Reflection;
using System.Text.RegularExpressions;
using Polyphony.Annotations;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Annotations;

/// <summary>
/// Drift guard for the runtime <c>app.Add&lt;TCommands&gt;("group")</c>
/// registrations in <c>src/Polyphony/Program.cs</c> against the
/// compile-time <c>[VerbGroup("group")]</c> attribute on each Commands
/// class. Without this guard the source generator would silently key
/// verbs under the wrong path when the two declarations diverge.
///
/// <para>Reads <c>Program.cs</c> as text (regex over registration lines)
/// rather than executing it, because <c>app.Add&lt;T&gt;</c> is a fluent
/// builder whose registrations are not introspectable at runtime.</para>
/// </summary>
public sealed class VerbGroupRegistrationDriftTests
{
    // app.Add<PlanCommands>("plan");      → group = "plan"
    // app.Add<ValidateCommand>();         → top-level (group = "")
    private static readonly Regex RegistrationRegex = new(
        @"app\.Add<(?<type>\w+)>\(\s*(?:""(?<group>[^""]*)"")?\s*\)\s*;",
        RegexOptions.Compiled);

    private static string FindProgramCs()
    {
        // Walk up from the test bin output looking for src/Polyphony/Program.cs.
        // AppContext.BaseDirectory points at .../tests/Polyphony.Tests/bin/Debug/net11.0/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Polyphony", "Program.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate src/Polyphony/Program.cs by walking up from " + AppContext.BaseDirectory);
    }

    private static IReadOnlyList<(string TypeName, string Group)> ReadRegistrations()
    {
        var text = File.ReadAllText(FindProgramCs());
        return RegistrationRegex.Matches(text)
            .Select(m => (TypeName: m.Groups["type"].Value, Group: m.Groups["group"].Value))
            .ToList();
    }

    private static Type? ResolveCommandsType(string typeName)
    {
        // Commands classes live under Polyphony.Commands; top-level command
        // classes also live there (HealthCommand, ValidateCommand, etc.).
        var asm = typeof(VerbGroupAttribute).Assembly;
        return asm.GetType($"Polyphony.Commands.{typeName}", throwOnError: false)
            ?? asm.GetTypes().FirstOrDefault(t => t.Name == typeName && t.IsClass);
    }

    [Fact]
    public void RegistrationRegex_FindsAtLeastTenEntries()
    {
        // Sanity guard against a regex change silently regressing the
        // whole suite to "0 registrations, 0 mismatches, all green".
        ReadRegistrations().Count.ShouldBeGreaterThan(10,
            "Expected to parse >10 app.Add<>() registrations from Program.cs.");
    }

    [Fact]
    public void EveryRegistration_HasMatchingVerbGroupAttribute()
    {
        var mismatches = new List<string>();

        foreach (var (typeName, group) in ReadRegistrations())
        {
            var type = ResolveCommandsType(typeName);
            if (type is null)
            {
                mismatches.Add($"app.Add<{typeName}>(\"{group}\") — type not found in Polyphony assembly");
                continue;
            }

            var attr = type.GetCustomAttribute<VerbGroupAttribute>();
            if (attr is null)
            {
                mismatches.Add($"{type.FullName} is registered as app.Add<>(\"{group}\") but has no [VerbGroup] attribute");
                continue;
            }

            if (attr.Name != group)
            {
                mismatches.Add($"{type.FullName} declares [VerbGroup(\"{attr.Name}\")] but Program.cs registers it as app.Add<>(\"{group}\")");
            }
        }

        mismatches.ShouldBeEmpty(string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void EveryVerbGroupAttribute_AppearsInProgramRegistrations()
    {
        // Inverse drift: a class carries [VerbGroup] but was never wired
        // into Program.cs — at runtime the verb is unreachable, but the
        // generator would still emit it into the catalog.
        var registrations = ReadRegistrations()
            .ToDictionary(r => r.TypeName, r => r.Group, StringComparer.Ordinal);

        var asm = typeof(VerbGroupAttribute).Assembly;
        var classesWithGroup = asm.GetTypes()
            .Where(t => t.IsClass && t.GetCustomAttribute<VerbGroupAttribute>() is not null)
            .ToList();

        var orphans = new List<string>();
        foreach (var cls in classesWithGroup)
        {
            if (!registrations.TryGetValue(cls.Name, out var registeredGroup))
            {
                orphans.Add($"{cls.FullName} carries [VerbGroup(\"{cls.GetCustomAttribute<VerbGroupAttribute>()!.Name}\")] " +
                            "but is not registered in Program.cs");
                continue;
            }

            var attrGroup = cls.GetCustomAttribute<VerbGroupAttribute>()!.Name;
            if (attrGroup != registeredGroup)
            {
                // Already covered by the converse test, but report so a single
                // run surfaces both directions of the mismatch.
                orphans.Add($"{cls.FullName} declares [VerbGroup(\"{attrGroup}\")] but Program.cs registers it as " +
                            $"app.Add<>(\"{registeredGroup}\")");
            }
        }

        orphans.ShouldBeEmpty(string.Join(Environment.NewLine, orphans));
    }
}
