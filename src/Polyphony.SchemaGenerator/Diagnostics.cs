using Microsoft.CodeAnalysis;

namespace Polyphony.SchemaGenerator;

/// <summary>
/// Diagnostic descriptors emitted by the verb output schema generator.
/// Codes <c>POLY1001</c>–<c>POLY1006</c> are reserved for this generator
/// (see <c>docs/decisions/verb-output-schema-registry.md</c> §
/// "Compile-time diagnostics").
/// </summary>
internal static class Diagnostics
{
    private const string Category = "Polyphony.SchemaGenerator";

    public static readonly DiagnosticDescriptor MissingVerbResult = new(
        id: "POLY1001",
        title: "Verb method must declare its result type",
        messageFormat: "[Command]-marked method '{0}' is missing [VerbResult(typeof(...))]; the verb output schema registry cannot include it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ResultTypeNotRegistered = new(
        id: "POLY1002",
        title: "Verb result type is not registered on PolyphonyJsonContext",
        messageFormat: "[VerbResult(typeof({0}))] on '{1}' references a type that is not [JsonSerializable]-registered on PolyphonyJsonContext; the verb cannot serialise its output at runtime",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingVerbGroup = new(
        id: "POLY1003",
        title: "Commands class containing [Command] methods must declare its verb group",
        messageFormat: "Class '{0}' contains [Command]-marked methods but is missing [VerbGroup(\"...\")] (use [VerbGroup(\"\")] for top-level commands)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ConflictingVerbGroup = new(
        id: "POLY1004",
        title: "Verb group declarations conflict across partial-class declarations",
        messageFormat: "Class '{0}' has multiple [VerbGroup] declarations across partial-class files with conflicting names: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CommandWithAliases = new(
        id: "POLY1005",
        title: "Verb output schema cannot key on a multi-alias command",
        messageFormat: "[Command(\"{0}\")] on '{1}' declares multiple aliases; the registry will key on the first segment ('{2}') only",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EmptyCommandName = new(
        id: "POLY1006",
        title: "Command name must not be empty",
        messageFormat: "[Command] on '{0}' has an empty or whitespace-only name",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
