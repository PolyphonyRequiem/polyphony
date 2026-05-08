// Polyfill: required to use C# 9+ `record` and `init` setters on
// netstandard2.0 (which doesn't define this type). Internal so it
// doesn't leak to consumers.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
