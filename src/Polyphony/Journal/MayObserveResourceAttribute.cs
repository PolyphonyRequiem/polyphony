namespace Polyphony.Journal;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MayObserveResourceAttribute(string kind) : Attribute
{
    public string Kind { get; } = kind;
}
