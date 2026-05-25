namespace Polyphony.Commands;

/// <summary>
/// Output of an internal verb method (the typed <c>*Async</c> seam introduced
/// by AB#3313): pairs the typed result record the verb computed with the
/// process exit code the public shell will return.
/// </summary>
/// <remarks>
/// The public verb method is a thin shell — parse envelope → call typed
/// <c>*Async</c> method → serialize <see cref="Result"/> to stdout → return
/// <see cref="ExitCode"/>. The typed method itself is pure of JSON shape,
/// CLI binding, and console I/O; callers like the future <c>PrLifecycle</c>
/// skeleton can compose typed without round-tripping through JSON.
/// </remarks>
internal sealed record CommandExecution<TResult>(TResult Result, int ExitCode);
