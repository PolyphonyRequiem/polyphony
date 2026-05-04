namespace Polyphony.Tests.Commands;

/// <summary>
/// Shared synchronization for tests that redirect <see cref="Console.Out"/>.
/// Uses a single <see cref="SemaphoreSlim"/> for both sync and async paths, because:
/// <list type="bullet">
///   <item><description>Async tests can't use <see cref="Monitor"/> (thread-affine; <c>await</c> may resume on a different thread).</description></item>
///   <item><description>Sync and async tests must serialize against each other — two separate primitives let parallel test classes stomp on <c>Console.Out</c>.</description></item>
/// </list>
/// All test classes that redirect <see cref="Console.Out"/> must acquire <see cref="AsyncLock"/>
/// (use <c>Wait()</c> for sync code, <c>WaitAsync()</c> for async).
/// <see cref="Lock"/> is retained as a no-op object reference for legacy <c>lock</c> blocks
/// that haven't migrated yet — those blocks must also acquire <see cref="AsyncLock"/> first.
/// </summary>
internal static class ConsoleTestLock
{
    internal static readonly object Lock = new();
    internal static readonly SemaphoreSlim AsyncLock = new(initialCount: 1, maxCount: 1);
}
