using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Polyphony.SchemaGenerator;

/// <summary>
/// Roslyn incremental source generator that produces
/// <c>Polyphony.VerbOutputSchemaCatalog</c> — a public C# class with a
/// JSON-serialised verb-output schema registry inferred from the
/// referencing compilation's <c>[VerbGroup]</c>, <c>[VerbResult]</c>,
/// and <c>[JsonSerializable]</c> attributes.
///
/// <para>
/// Implements the design ADR'd at
/// <c>docs/decisions/verb-output-schema-registry.md</c>. See diagnostic
/// codes <c>POLY1001</c>–<c>POLY1006</c> in <see cref="Diagnostics"/>.
/// </para>
///
/// <para>
/// Discovery strategy: walk the compilation's class/method symbols
/// directly (via <see cref="IIncrementalGenerator"/> +
/// <see cref="IncrementalGeneratorInitializationContext.CompilationProvider"/>).
/// For ~150 types / ~86 methods this is well within budget; the
/// IIncrementalGenerator surface is preserved so a future
/// fast-path can use <c>ForAttributeWithMetadataName</c> if needed.
/// </para>
/// </summary>
[Generator]
public sealed class VerbSchemaGenerator : IIncrementalGenerator
{
    private const string VerbGroupAttrName = "Polyphony.Annotations.VerbGroupAttribute";
    private const string VerbResultAttrName = "Polyphony.Annotations.VerbResultAttribute";
    private const string CommandAttrShortName = "CommandAttribute";
    private const string CommandAttrShortAlias = "Command";
    private const string JsonSerializableAttrName = "System.Text.Json.Serialization.JsonSerializableAttribute";
    private const string JsonPropertyNameAttrName = "System.Text.Json.Serialization.JsonPropertyNameAttribute";
    private const string JsonIgnoreAttrName = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string JsonSerializerContextFqn = "System.Text.Json.Serialization.JsonSerializerContext";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var pipeline = context.CompilationProvider;
        context.RegisterSourceOutput(pipeline, Generate);
    }

    private static void Generate(SourceProductionContext spc, Compilation compilation)
    {
        var verbGroupAttr = compilation.GetTypeByMetadataName(VerbGroupAttrName);
        var verbResultAttr = compilation.GetTypeByMetadataName(VerbResultAttrName);
        var jsonSerializableAttr = compilation.GetTypeByMetadataName(JsonSerializableAttrName);

        // Polyphony's own attributes must exist; if not, this isn't the
        // polyphony assembly and we have nothing to do. Silent-skip.
        if (verbGroupAttr is null || verbResultAttr is null || jsonSerializableAttr is null)
        {
            EmitEmptyCatalog(spc);
            return;
        }

        // 1. Discover JsonSerializerContext-registered types.
        var registeredTypes = DiscoverRegisteredTypes(compilation, jsonSerializableAttr);

        // 2. Walk all class symbols, find Commands classes (those with
        //    [Command]-marked methods or [VerbGroup]).
        var commandClasses = DiscoverCommandClasses(compilation);

        // 3. Build VerbInfos + emit diagnostics for each class.
        var verbs = new List<VerbInfo>();
        foreach (var classInfo in commandClasses)
        {
            CheckGroupAttribute(spc, classInfo, verbGroupAttr);
            CollectVerbsFromClass(spc, classInfo, verbResultAttr, registeredTypes, verbs);
        }

        // 4. Walk DTO graphs from each verb's result type to fill the
        //    types map. BFS: include nested record types too.
        var typeInfos = WalkDtoGraphs(verbs, registeredTypes);

        // 5. Emit the C# source.
        EmitCatalog(spc, verbs, typeInfos);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Discovery
    // ─────────────────────────────────────────────────────────────────────

    private static HashSet<INamedTypeSymbol> DiscoverRegisteredTypes(
        Compilation compilation, INamedTypeSymbol jsonSerializableAttr)
    {
        // Walk every type in the polyphony assembly; find the partial
        // class derived from JsonSerializerContext; read its
        // [JsonSerializable] attributes. Polyphony has exactly one such
        // context (PolyphonyJsonContext); we don't need to scan
        // referenced assemblies.
        var contextBase = compilation.GetTypeByMetadataName(JsonSerializerContextFqn);
        var registered = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var type in EnumerateAssemblyTypes(compilation.Assembly))
        {
            if (contextBase is null || !DerivesFrom(type, contextBase))
            {
                continue;
            }

            foreach (var attr in type.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attr.AttributeClass, jsonSerializableAttr))
                {
                    continue;
                }

                if (attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is INamedTypeSymbol regType)
                {
                    registered.Add(regType);
                }
            }
        }

        return registered;
    }

    private static List<CommandClassInfo> DiscoverCommandClasses(Compilation compilation)
    {
        var results = new List<CommandClassInfo>();

        foreach (var type in EnumerateAssemblyTypes(compilation.Assembly))
        {
            if (type.TypeKind != TypeKind.Class || type.IsStatic)
            {
                continue;
            }

            // A "Commands class" is one that either has [VerbGroup] or
            // contains at least one [Command]-marked method. We discover
            // both so that a forgotten [VerbGroup] surfaces POLY1003.
            var commandMethods = new List<CommandMethodInfo>();
            foreach (var member in type.GetMembers())
            {
                if (member is not IMethodSymbol method)
                {
                    continue;
                }

                var commandAttr = method.GetAttributes().FirstOrDefault(IsCommandAttribute);
                if (commandAttr is null)
                {
                    continue;
                }

                var commandName = ReadCommandName(commandAttr);
                commandMethods.Add(new CommandMethodInfo(method, commandAttr, commandName));
            }

            if (commandMethods.Count == 0)
            {
                continue;
            }

            results.Add(new CommandClassInfo(type, commandMethods));
        }

        return results;
    }

    private static bool IsCommandAttribute(AttributeData attr)
    {
        // Syntax-style check: ConsoleAppFramework's CommandAttribute is
        // generated by its source generator, so the symbol may not
        // resolve cleanly across generator-pass boundaries. Match by
        // unqualified name + the conventional namespace, accepting
        // either the full or short form.
        var ac = attr.AttributeClass;
        if (ac is null)
        {
            return false;
        }
        if (ac.Name is CommandAttrShortName or CommandAttrShortAlias)
        {
            var ns = ac.ContainingNamespace?.ToDisplayString() ?? "";
            return ns is "ConsoleAppFramework" or "";
        }
        return false;
    }

    private static string ReadCommandName(AttributeData attr)
    {
        if (attr.ConstructorArguments.Length == 0)
        {
            return "";
        }
        return attr.ConstructorArguments[0].Value as string ?? "";
    }

    // ─────────────────────────────────────────────────────────────────────
    // Diagnostics + verb assembly
    // ─────────────────────────────────────────────────────────────────────

    private static void CheckGroupAttribute(
        SourceProductionContext spc, CommandClassInfo info, INamedTypeSymbol verbGroupAttr)
    {
        var groupAttrs = info.ClassSymbol.GetAttributes()
            .Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, verbGroupAttr))
            .ToList();

        if (groupAttrs.Count == 0)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.MissingVerbGroup,
                info.ClassSymbol.Locations.FirstOrDefault(),
                info.ClassSymbol.ToDisplayString()));
            return;
        }

        var distinctNames = groupAttrs
            .Select(a => a.ConstructorArguments.Length > 0 ? a.ConstructorArguments[0].Value as string ?? "" : "")
            .Distinct()
            .ToList();

        if (distinctNames.Count > 1)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.ConflictingVerbGroup,
                info.ClassSymbol.Locations.FirstOrDefault(),
                info.ClassSymbol.ToDisplayString(),
                string.Join(", ", distinctNames.Select(n => $"\"{n}\""))));
        }
    }

    private static void CollectVerbsFromClass(
        SourceProductionContext spc,
        CommandClassInfo classInfo,
        INamedTypeSymbol verbResultAttr,
        HashSet<INamedTypeSymbol> registered,
        List<VerbInfo> verbs)
    {
        var groupName = ReadGroupName(classInfo.ClassSymbol);

        foreach (var cmd in classInfo.CommandMethods)
        {
            if (string.IsNullOrWhiteSpace(cmd.CommandName))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.EmptyCommandName,
                    cmd.Method.Locations.FirstOrDefault(),
                    cmd.Method.ToDisplayString()));
                continue;
            }

            var commandPath = cmd.CommandName;
            if (commandPath.Contains('|'))
            {
                var primary = commandPath.Split('|')[0];
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.CommandWithAliases,
                    cmd.Method.Locations.FirstOrDefault(),
                    cmd.CommandName,
                    cmd.Method.ToDisplayString(),
                    primary));
                commandPath = primary;
            }

            var verbResultData = cmd.Method.GetAttributes().FirstOrDefault(
                a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, verbResultAttr));

            if (verbResultData is null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.MissingVerbResult,
                    cmd.Method.Locations.FirstOrDefault(),
                    cmd.Method.ToDisplayString()));
                continue;
            }

            if (verbResultData.ConstructorArguments.Length == 0 ||
                verbResultData.ConstructorArguments[0].Value is not INamedTypeSymbol resultType)
            {
                continue;
            }

            if (!registered.Contains(resultType))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.ResultTypeNotRegistered,
                    cmd.Method.Locations.FirstOrDefault(),
                    resultType.ToDisplayString(),
                    cmd.Method.ToDisplayString()));
                continue;
            }

            var verbPath = string.IsNullOrEmpty(groupName) ? commandPath : $"{groupName} {commandPath}";

            verbs.Add(new VerbInfo(
                VerbPath: verbPath,
                GroupName: groupName,
                CommandName: commandPath,
                CommandClassName: classInfo.ClassSymbol.ToDisplayString(),
                ResultTypeFullName: resultType.ToDisplayString(),
                Inputs: ExtractInputs(cmd.Method)));
        }
    }

    /// <summary>
    /// Project a verb method's parameter list into the registry's
    /// <see cref="InputInfo"/> shape. Filters out the conventional
    /// <c>CancellationToken ct = default</c> trailer (not a user-facing
    /// flag) and renames each parameter from PascalCase to kebab-case
    /// to mirror ConsoleAppFramework's flag mapping.
    ///
    /// <para>Move #2 note: the schema's <c>required</c> flag is
    /// <c>!HasExplicitDefaultValue</c> — i.e. it reflects CAF's view of
    /// the API surface, not the verb's internal <c>HaltIfMissing</c>
    /// list. A param defaulted to a Move #2 sentinel
    /// (<c>int.MinValue</c> or <c>""</c>) appears as <c>required:
    /// false</c> in the schema even though the verb body may halt on
    /// it. The runtime envelope is the safety net — the schema's role
    /// is to document the CAF surface only.</para>
    /// </summary>
    private static System.Collections.Immutable.ImmutableArray<InputInfo> ExtractInputs(IMethodSymbol method)
    {
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<InputInfo>();
        foreach (var param in method.Parameters)
        {
            if (IsCancellationToken(param.Type))
            {
                continue;
            }
            builder.Add(new InputInfo(
                Name: ToKebabCase(param.Name),
                ClrType: param.Type.ToDisplayString(),
                Required: !param.HasExplicitDefaultValue,
                DefaultLiteral: param.HasExplicitDefaultValue
                    ? EncodeDefaultLiteral(param.ExplicitDefaultValue)
                    : null));
        }
        return builder.ToImmutable();
    }

    private static bool IsCancellationToken(ITypeSymbol type)
    {
        return type is INamedTypeSymbol nt
            && nt.Name == "CancellationToken"
            && (nt.ContainingNamespace?.ToDisplayString() ?? "") == "System.Threading";
    }

    private static string ToKebabCase(string identifier)
    {
        // PascalCase / camelCase → kebab-case (matches ConsoleAppFramework's
        // flag generation): split before each capital that follows a
        // lowercase or digit, lowercase the whole thing.
        // Adjacent capitals collapse to a single segment to leave acronyms
        // intact (e.g. "ioPath" → "io-path", "URL" stays "url").
        if (string.IsNullOrEmpty(identifier))
        {
            return identifier;
        }
        var sb = new StringBuilder(identifier.Length + 4);
        for (var i = 0; i < identifier.Length; i++)
        {
            var ch = identifier[i];
            if (char.IsUpper(ch))
            {
                if (i > 0 && (char.IsLower(identifier[i - 1]) || char.IsDigit(identifier[i - 1])))
                {
                    sb.Append('-');
                }
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static string EncodeDefaultLiteral(object? value)
    {
        if (value is null)
        {
            return "null";
        }
        return value switch
        {
            bool b => b ? "true" : "false",
            string s => JsonEncode(s),
            char c => JsonEncode(c.ToString()),
            // Numeric primitives format identically to JSON.
            sbyte or byte or short or ushort or int or uint or long or ulong
                => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // Enums (and any other value) — fall back to the string form.
            _ => JsonEncode(value.ToString() ?? ""),
        };
    }

    private static string ReadGroupName(INamedTypeSymbol classSymbol)
    {
        var attr = classSymbol.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == VerbGroupAttrName);
        if (attr is null || attr.ConstructorArguments.Length == 0)
        {
            return "";
        }
        return attr.ConstructorArguments[0].Value as string ?? "";
    }

    // ─────────────────────────────────────────────────────────────────────
    // DTO graph walking
    // ─────────────────────────────────────────────────────────────────────

    private static Dictionary<string, TypeInfo> WalkDtoGraphs(
        List<VerbInfo> verbs, HashSet<INamedTypeSymbol> registered)
    {
        var typeMap = new Dictionary<string, TypeInfo>(System.StringComparer.Ordinal);
        var queue = new Queue<INamedTypeSymbol>();
        var visited = new HashSet<string>(System.StringComparer.Ordinal);

        var fqnToSymbol = registered.ToDictionary(
            t => t.ToDisplayString(),
            t => t,
            System.StringComparer.Ordinal);

        foreach (var verb in verbs)
        {
            if (fqnToSymbol.TryGetValue(verb.ResultTypeFullName, out var sym) && visited.Add(sym.ToDisplayString()))
            {
                queue.Enqueue(sym);
            }
        }

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            var fields = ImmutableArray.CreateBuilder<FieldInfo>();

            foreach (var member in type.GetMembers())
            {
                if (member is not IPropertySymbol prop || prop.IsStatic || prop.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                if (HasJsonIgnoreAlways(prop))
                {
                    continue;
                }

                var fieldInfo = AnalyzeProperty(prop);
                fields.Add(fieldInfo);

                // Enqueue nested type refs for BFS traversal.
                EnqueueIfReferenceType(fieldInfo.TypeRef, fqnToSymbol, visited, queue);
                EnqueueIfReferenceType(fieldInfo.ElementTypeRef, fqnToSymbol, visited, queue);
                EnqueueIfReferenceType(fieldInfo.ValueTypeRef, fqnToSymbol, visited, queue);
            }

            typeMap[type.ToDisplayString()] = new TypeInfo(type.ToDisplayString(), fields.ToImmutable());
        }

        return typeMap;
    }

    private static void EnqueueIfReferenceType(
        string? fqn,
        Dictionary<string, INamedTypeSymbol> registered,
        HashSet<string> visited,
        Queue<INamedTypeSymbol> queue)
    {
        if (fqn is null)
        {
            return;
        }
        if (registered.TryGetValue(fqn, out var sym) && visited.Add(fqn))
        {
            queue.Enqueue(sym);
        }
    }

    private static FieldInfo AnalyzeProperty(IPropertySymbol prop)
    {
        var name = ResolveJsonName(prop);
        var (kind, typeRef, elementKind, elementClr, elementRef, keyKind, keyClr, valueKind, valueClr, valueRef) = ClassifyType(prop.Type);
        var nullableAnn = prop.NullableAnnotation.ToString();
        var ignore = ResolveIgnoreCondition(prop);
        var canOmit = ComputeCanOmitWhenNull(prop, ignore);

        return new FieldInfo(
            Name: name,
            Kind: kind,
            ClrType: prop.Type.ToDisplayString(),
            NullableAnnotation: nullableAnn,
            IgnoreCondition: ignore,
            CanOmitWhenNull: canOmit,
            TypeRef: typeRef,
            ElementKind: elementKind,
            ElementClrType: elementClr,
            ElementTypeRef: elementRef,
            KeyKind: keyKind,
            KeyClrType: keyClr,
            ValueKind: valueKind,
            ValueClrType: valueClr,
            ValueTypeRef: valueRef);
    }

    private static (FieldKind kind, string? typeRef,
                    FieldKind? elementKind, string? elementClr, string? elementRef,
                    FieldKind? keyKind, string? keyClr,
                    FieldKind? valueKind, string? valueClr, string? valueRef)
        ClassifyType(ITypeSymbol type)
    {
        // Strip Nullable<T>; nullable_annotation already captures it.
        if (type is INamedTypeSymbol nt && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            type = nt.TypeArguments[0];
        }

        // Map (Dictionary<K, V> / IReadOnlyDictionary<K, V> / IDictionary<K, V>)
        if (TryGetMapTypes(type, out var keyType, out var valueType))
        {
            var (vKind, vRef, _, _, _, _, _, _, _, _) = ClassifyType(valueType);
            var (kKind, _, _, _, _, _, _, _, _, _) = ClassifyType(keyType);
            return (
                FieldKind.Map,
                null,
                null, null, null,
                kKind, keyType.ToDisplayString(),
                vKind, vKind == FieldKind.Object ? null : valueType.ToDisplayString(),
                vKind == FieldKind.Object ? vRef : null);
        }

        // List / array (IEnumerable<T> / IReadOnlyList<T> / List<T> / T[])
        if (TryGetElementType(type, out var elementType))
        {
            var (eKind, eRef, _, _, _, _, _, _, _, _) = ClassifyType(elementType);
            return (
                FieldKind.List,
                null,
                eKind,
                eKind == FieldKind.Object ? null : elementType.ToDisplayString(),
                eKind == FieldKind.Object ? eRef : null,
                null, null,
                null, null, null);
        }

        // Object (named record/class that isn't a primitive/string/etc)
        if (IsObjectType(type))
        {
            return (FieldKind.Object, type.ToDisplayString(), null, null, null, null, null, null, null, null);
        }

        // Scalar
        return (FieldKind.Scalar, null, null, null, null, null, null, null, null, null);
    }

    private static bool IsObjectType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return false;
        }
        if (type.SpecialType != SpecialType.None)
        {
            return false; // Int32, String, Boolean, etc.
        }
        if (type is INamedTypeSymbol named)
        {
            // Common scalar-ish types in System namespace.
            var fqn = named.ToDisplayString();
            return fqn switch
            {
                "System.DateTime" or "System.DateTimeOffset" or "System.TimeSpan"
                    or "System.Guid" or "System.Uri" or "System.Decimal"
                    or "System.DateOnly" or "System.TimeOnly" => false,
                _ => named.TypeKind == TypeKind.Class || named.TypeKind == TypeKind.Struct,
            };
        }
        return false;
    }

    private static bool TryGetMapTypes(ITypeSymbol type, out ITypeSymbol keyType, out ITypeSymbol valueType)
    {
        keyType = null!;
        valueType = null!;
        if (type is not INamedTypeSymbol named || !named.IsGenericType)
        {
            return false;
        }
        var def = named.OriginalDefinition.ToDisplayString();
        if (def is "System.Collections.Generic.Dictionary<TKey, TValue>"
                or "System.Collections.Generic.IDictionary<TKey, TValue>"
                or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>")
        {
            keyType = named.TypeArguments[0];
            valueType = named.TypeArguments[1];
            return true;
        }
        return false;
    }

    private static bool TryGetElementType(ITypeSymbol type, out ITypeSymbol elementType)
    {
        elementType = null!;

        if (type is IArrayTypeSymbol array)
        {
            elementType = array.ElementType;
            return true;
        }

        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var def = named.OriginalDefinition.ToDisplayString();
            if (def is "System.Collections.Generic.IEnumerable<T>"
                    or "System.Collections.Generic.IList<T>"
                    or "System.Collections.Generic.IReadOnlyList<T>"
                    or "System.Collections.Generic.IReadOnlyCollection<T>"
                    or "System.Collections.Generic.ICollection<T>"
                    or "System.Collections.Generic.List<T>"
                    or "System.Collections.Immutable.ImmutableArray<T>"
                    or "System.Collections.Immutable.ImmutableList<T>")
            {
                elementType = named.TypeArguments[0];
                return true;
            }
        }

        return false;
    }

    private static string ResolveJsonName(IPropertySymbol prop)
    {
        // Per-property [JsonPropertyName("...")] override.
        var attr = prop.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == JsonPropertyNameAttrName);
        if (attr is not null && attr.ConstructorArguments.Length > 0 &&
            attr.ConstructorArguments[0].Value is string overrideName)
        {
            return overrideName;
        }

        // Context-level naming policy is SnakeCaseLower (hardcoded —
        // PolyphonyJsonContext is the only context).
        return ToSnakeCaseLower(prop.Name);
    }

    /// <summary>
    /// Decides whether the field can legitimately be absent from the
    /// serialized JSON envelope — the signal that drives the workflow
    /// resolver lint's JINJA002 "wrap in a guard" warning.
    ///
    /// <para>The historical (buggy) implementation returned
    /// <c>ignore != "Never"</c>, which marked every field omittable
    /// because the global serializer policy is <c>WhenWritingNull</c>.
    /// That conflated "what STJ does on null" with "can the value
    /// actually be null at runtime", and produced 200+ false-positive
    /// JINJA002 warnings against fields like
    /// <c>required string State</c> that the C# nullable-reference
    /// type system already proves non-null.</para>
    ///
    /// <para>The correct logic respects nullable annotations
    /// (project-wide <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c> +
    /// <c>TreatWarningsAsErrors</c> means an unannotated reference
    /// type is compiler-guaranteed non-null at construction):</para>
    /// <list type="bullet">
    ///   <item><c>Always</c> → omittable (the field is filtered out unconditionally).</item>
    ///   <item><c>Never</c> → never omitted (overrides the global policy).</item>
    ///   <item><c>WhenWritingNull</c> → omittable only when the type can hold null
    ///         (nullable reference type or <c>Nullable&lt;T&gt;</c>).</item>
    ///   <item><c>WhenWritingDefault</c> → omittable for value types (<c>0</c>,
    ///         <c>false</c>, <c>default(struct)</c>) and for nullable reference
    ///         types (where <c>default</c> is null). Non-nullable reference types
    ///         have <c>default == null</c>, but the type contract excludes that
    ///         value, so the field cannot legitimately be omitted.</item>
    /// </list>
    /// </summary>
    private static bool ComputeCanOmitWhenNull(IPropertySymbol prop, string ignore)
    {
        var isNullableRef =
            prop.Type.IsReferenceType
            && prop.NullableAnnotation == NullableAnnotation.Annotated;
        var isNullableValue =
            prop.Type is INamedTypeSymbol nt
            && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

        return ignore switch
        {
            "Always" => true,
            "Never" => false,
            "WhenWritingNull" => isNullableRef || isNullableValue,
            "WhenWritingDefault" => prop.Type.IsValueType || isNullableRef,
            _ => true, // unknown / future enum values — fail safe (warn).
        };
    }

    private static string ResolveIgnoreCondition(IPropertySymbol prop)
    {
        var attr = prop.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == JsonIgnoreAttrName);
        if (attr is null)
        {
            return "WhenWritingNull"; // PolyphonyJsonContext default.
        }

        // [JsonIgnore] with no Condition → always ignored. Skip these properties
        // in the field walker (they're filtered earlier).
        var condition = attr.NamedArguments.FirstOrDefault(kv => kv.Key == "Condition");
        if (condition.Key is null || condition.Value.Value is not int enumValue)
        {
            return "Always";
        }

        // System.Text.Json.Serialization.JsonIgnoreCondition enum values:
        // Never=0, Always=1, WhenWritingDefault=2, WhenWritingNull=3.
        return enumValue switch
        {
            0 => "Never",
            1 => "Always",
            2 => "WhenWritingDefault",
            3 => "WhenWritingNull",
            _ => "Unknown",
        };
    }

    private static bool HasJsonIgnoreAlways(IPropertySymbol prop)
    {
        var attr = prop.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == JsonIgnoreAttrName);
        if (attr is null)
        {
            return false;
        }
        var condition = attr.NamedArguments.FirstOrDefault(kv => kv.Key == "Condition");
        if (condition.Key is null)
        {
            return true; // No Condition → Always (the default for JsonIgnore).
        }
        return condition.Value.Value is int v && v == 1; // 1 == Always
    }

    private static string ToSnakeCaseLower(string s)
    {
        // PascalCase → snake_case_lower. Matches
        // JsonNamingPolicy.SnakeCaseLower's behavior closely enough for
        // the property-name shapes polyphony uses (PascalCase, no
        // initialisms).
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    var prev = s[i - 1];
                    var next = i + 1 < s.Length ? s[i + 1] : '\0';
                    if (char.IsLower(prev) || (char.IsUpper(prev) && char.IsLower(next)))
                    {
                        sb.Append('_');
                    }
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static IEnumerable<INamedTypeSymbol> EnumerateAssemblyTypes(IAssemblySymbol assembly)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            foreach (var nested in ns.GetNamespaceMembers())
            {
                stack.Push(nested);
            }
            foreach (var type in ns.GetTypeMembers())
            {
                yield return type;
                foreach (var inner in EnumerateNested(type))
                {
                    yield return inner;
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNested(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNested(nested))
            {
                yield return deeper;
            }
        }
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
            current = current.BaseType;
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Emission
    // ─────────────────────────────────────────────────────────────────────

    private static void EmitCatalog(
        SourceProductionContext spc,
        List<VerbInfo> verbs,
        Dictionary<string, TypeInfo> types)
    {
        var json = SerializeRegistry(verbs, types);
        var source = $$""""
            // <auto-generated/>
            // Generated by Polyphony.SchemaGenerator. Do not edit.
            // See docs/decisions/verb-output-schema-registry.md.
            #nullable enable

            namespace Polyphony;

            /// <summary>
            /// Verb output schema registry, generated at compile time from
            /// <see cref="PolyphonyJsonContext"/>'s <c>[JsonSerializable]</c>
            /// attributes and the <c>[VerbGroup]</c> / <c>[VerbResult]</c>
            /// attributes on each Commands class.
            /// <para>The <see cref="Json"/> constant is the in-memory source of
            /// truth; <c>Polyphony.SchemaExporter</c> writes it to
            /// <c>artifacts/verb-output-schemas.json</c> for the
            /// workflow-YAML resolver lint.</para>
            /// </summary>
            public static class VerbOutputSchemaCatalog
            {
                public const string Json = """{{json}}""";
            }
            """";
        spc.AddSource("VerbOutputSchemaCatalog.g.cs", source);
    }

    private static void EmitEmptyCatalog(SourceProductionContext spc)
    {
        const string source = """
            // <auto-generated/>
            // Polyphony.SchemaGenerator: not the polyphony assembly; emitting empty catalog.
            #nullable enable
            namespace Polyphony;
            internal static class VerbOutputSchemaCatalog
            {
                public const string Json = "{\"version\":1,\"verbs\":{},\"types\":{}}";
            }
            """;
        spc.AddSource("VerbOutputSchemaCatalog.g.cs", source);
    }

    private static string SerializeRegistry(List<VerbInfo> verbs, Dictionary<string, TypeInfo> types)
    {
        // We hand-emit JSON to avoid pulling System.Text.Json as a
        // generator-side runtime dependency (the generator targets
        // netstandard2.0 and STJ versions can drift). The shape is
        // documented in docs/decisions/verb-output-schema-registry.md.
        var sb = new StringBuilder(1024);
        sb.Append("{\"version\":1,\"verbs\":{");
        var first = true;
        foreach (var verb in verbs.OrderBy(v => v.VerbPath, System.StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonEncode(verb.VerbPath)).Append(":{");
            sb.Append("\"result_type\":").Append(JsonEncode(verb.ResultTypeFullName)).Append(',');
            sb.Append("\"command_class\":").Append(JsonEncode(verb.CommandClassName)).Append(',');
            sb.Append("\"inputs\":[");
            var firstInput = true;
            foreach (var inp in verb.Inputs)
            {
                if (!firstInput) sb.Append(',');
                firstInput = false;
                sb.Append('{');
                sb.Append("\"name\":").Append(JsonEncode(inp.Name)).Append(',');
                sb.Append("\"clr_type\":").Append(JsonEncode(inp.ClrType)).Append(',');
                sb.Append("\"required\":").Append(inp.Required ? "true" : "false");
                if (inp.DefaultLiteral is not null)
                {
                    sb.Append(",\"default\":").Append(inp.DefaultLiteral);
                }
                sb.Append('}');
            }
            sb.Append(']');
            sb.Append('}');
        }
        sb.Append("},\"types\":{");
        first = true;
        foreach (var typeInfo in types.Values.OrderBy(t => t.FullName, System.StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonEncode(typeInfo.FullName)).Append(":{\"fields\":[");
            var firstField = true;
            foreach (var f in typeInfo.Fields)
            {
                if (!firstField) sb.Append(',');
                firstField = false;
                sb.Append('{');
                sb.Append("\"name\":").Append(JsonEncode(f.Name)).Append(',');
                sb.Append("\"kind\":").Append(JsonEncode(f.Kind.ToString().ToLowerInvariant())).Append(',');
                sb.Append("\"clr_type\":").Append(JsonEncode(f.ClrType)).Append(',');
                sb.Append("\"nullable_annotation\":").Append(JsonEncode(f.NullableAnnotation)).Append(',');
                sb.Append("\"ignore_condition\":").Append(JsonEncode(f.IgnoreCondition)).Append(',');
                sb.Append("\"can_omit_when_null\":").Append(f.CanOmitWhenNull ? "true" : "false");
                if (f.TypeRef is not null)
                {
                    sb.Append(",\"type_ref\":").Append(JsonEncode(f.TypeRef));
                }
                if (f.ElementKind is not null)
                {
                    sb.Append(",\"element_kind\":").Append(JsonEncode(f.ElementKind.Value.ToString().ToLowerInvariant()));
                }
                if (f.ElementClrType is not null)
                {
                    sb.Append(",\"element_clr_type\":").Append(JsonEncode(f.ElementClrType));
                }
                if (f.ElementTypeRef is not null)
                {
                    sb.Append(",\"element_type_ref\":").Append(JsonEncode(f.ElementTypeRef));
                }
                if (f.KeyKind is not null)
                {
                    sb.Append(",\"key_kind\":").Append(JsonEncode(f.KeyKind.Value.ToString().ToLowerInvariant()));
                }
                if (f.KeyClrType is not null)
                {
                    sb.Append(",\"key_clr_type\":").Append(JsonEncode(f.KeyClrType));
                }
                if (f.ValueKind is not null)
                {
                    sb.Append(",\"value_kind\":").Append(JsonEncode(f.ValueKind.Value.ToString().ToLowerInvariant()));
                }
                if (f.ValueClrType is not null)
                {
                    sb.Append(",\"value_clr_type\":").Append(JsonEncode(f.ValueClrType));
                }
                if (f.ValueTypeRef is not null)
                {
                    sb.Append(",\"value_type_ref\":").Append(JsonEncode(f.ValueTypeRef));
                }
                sb.Append('}');
            }
            sb.Append("]}");
        }
        sb.Append("}}");
        return sb.ToString();
    }

    private static string JsonEncode(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.AppendFormat("\\u{0:x4}", (int)c);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // Internal records used during generation.
    private sealed record CommandClassInfo(
        INamedTypeSymbol ClassSymbol,
        List<CommandMethodInfo> CommandMethods);

    private sealed record CommandMethodInfo(
        IMethodSymbol Method,
        AttributeData CommandAttribute,
        string CommandName);
}
