namespace Polyphony.Annotations;

/// <summary>
/// Declares the verb-group prefix for a Commands class. The
/// <see cref="Polyphony.SchemaGenerator"/> source generator reads this
/// at compile time to synthesize the canonical verb path
/// (<c>"&lt;group&gt; &lt;command-name&gt;"</c>) for each method bearing
/// <see cref="ConsoleAppFramework.CommandAttribute"/>.
///
/// <para>
/// Must match the runtime registration in <c>Program.cs</c>:
/// <c>app.Add&lt;TCommands&gt;("&lt;group&gt;")</c>. Drift between the
/// attribute and the registration is caught by the
/// <c>VerbGroupRegistrationDriftTests</c> integration test.
/// </para>
///
/// <para>
/// Top-level commands registered via <c>app.Add&lt;T&gt;()</c> with no
/// prefix carry <see cref="VerbGroupAttribute"/> with an empty string.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VerbGroupAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
