namespace Polyphony.Journal.Reset;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ExcludedFromProjectionResetAttribute(string kind, string reason) : Attribute
{
    public string Kind { get; } = kind;
    public string Reason { get; } = reason;
}
