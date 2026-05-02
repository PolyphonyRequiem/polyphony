namespace Polyphony.Tests.Commands;

/// <summary>
/// Shared lock object for tests that redirect <see cref="Console.Out"/>.
/// All test classes must acquire this lock before changing Console.Out to
/// prevent parallel test runs from corrupting each other's output capture.
/// </summary>
internal static class ConsoleTestLock
{
    internal static readonly object Lock = new();
}
