namespace Polyphony.Journal;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class JournaledActionAttribute : Attribute
{
    public string? Action { get; init; }
}
