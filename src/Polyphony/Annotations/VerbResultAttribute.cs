namespace Polyphony.Annotations;

/// <summary>
/// Declares the result DTO type emitted by a verb method. The
/// <see cref="Polyphony.SchemaGenerator"/> source generator reads this
/// at compile time to build the verb→DTO mapping in the verb output
/// schema registry.
///
/// <para>
/// The referenced type MUST be registered on
/// <c>PolyphonyJsonContext</c> via <c>[JsonSerializable(typeof(...))]</c>
/// or the generator emits diagnostic <c>POLY1002</c>.
/// </para>
///
/// <para>
/// The pattern at the call site:
/// <code>
/// [Command("compose-addendum")]
/// [VerbResult(typeof(AgentComposeAddendumResult))]
/// public async Task&lt;int&gt; ComposeAddendum(...) { ... }
/// </code>
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class VerbResultAttribute(Type resultType) : Attribute
{
    public Type ResultType { get; } = resultType;
}
