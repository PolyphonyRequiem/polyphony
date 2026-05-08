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
    string ResultTypeFullName  // "Polyphony.Models.AgentComposeAddendumResult"
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
