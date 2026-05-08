using Microsoft.CodeAnalysis;

namespace Polyphony.SchemaGenerator;

/// <summary>
/// Internal models for the verb output schema generator.
///
/// Records intentionally lean — they're consumed by the JSON emitter
/// and the diagnostics path only. Roslyn symbols are NOT held across
/// the incremental pipeline boundary (they're not equatable for
/// caching purposes); the symbols are read in <c>SyntaxProvider</c>
/// lambdas and projected into these records immediately.
/// </summary>
internal sealed record VerbInfo(
    string VerbPath,           // "agent compose-addendum"
    string GroupName,          // "agent" (or "" for top-level)
    string CommandName,        // "compose-addendum"
    string CommandClassName,   // "Polyphony.Commands.AgentCommands"
    string ResultTypeFullName, // "Polyphony.Models.AgentComposeAddendumResult"
    System.Collections.Immutable.ImmutableArray<InputInfo> Inputs
);

/// <summary>
/// One CLI parameter on a verb's method signature. Mirrors
/// ConsoleAppFramework's PascalCase→kebab-case flag mapping (e.g.
/// <c>int prNumber</c> → <c>--pr-number</c>) so the workflow author
/// lint can cross-check verb call-site arg shapes against the
/// authoritative C# signature.
/// </summary>
/// <param name="Name">CLI flag name in kebab-case (no leading <c>--</c>).</param>
/// <param name="ClrType">Roslyn display string for the parameter type.</param>
/// <param name="Required"><c>true</c> when the parameter has no default value.</param>
/// <param name="DefaultLiteral">JSON-encoded literal of the default
/// value when <see cref="Required"/> is <c>false</c>; <c>null</c>
/// otherwise. Strings are quoted, primitives are bare, <c>null</c>
/// defaults serialize as the JSON token <c>null</c>.</param>
internal sealed record InputInfo(
    string Name,
    string ClrType,
    bool Required,
    string? DefaultLiteral
);

internal sealed record FieldInfo(
    string Name,                       // JSON wire name (after naming policy)
    FieldKind Kind,
    string ClrType,                    // for diagnostics
    string NullableAnnotation,         // "Annotated" / "NotAnnotated" / "None"
    string IgnoreCondition,            // "Always" / "Never" / "WhenWritingDefault" / "WhenWritingNull"
    bool CanOmitWhenNull,
    string? TypeRef,                   // for Kind=Object: nested type FQN
    FieldKind? ElementKind,            // for Kind=List
    string? ElementClrType,            // for Kind=List
    string? ElementTypeRef,            // for Kind=List of object
    FieldKind? KeyKind,                // for Kind=Map
    string? KeyClrType,                // for Kind=Map
    FieldKind? ValueKind,              // for Kind=Map
    string? ValueClrType,              // for Kind=Map (when value is scalar)
    string? ValueTypeRef               // for Kind=Map of object
);

internal enum FieldKind
{
    Scalar,
    Object,
    List,
    Map
}

internal sealed record TypeInfo(
    string FullName,                  // "Polyphony.Models.PlanDeriveAncestorChainResult"
    System.Collections.Immutable.ImmutableArray<FieldInfo> Fields
);
