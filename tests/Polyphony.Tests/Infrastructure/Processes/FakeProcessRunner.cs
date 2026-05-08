using Polyphony.Infrastructure.Processes;

namespace Polyphony.Tests.Infrastructure.Processes;

/// <summary>
/// In-memory <see cref="IProcessRunner"/> for unit-testing typed clients
/// without spawning real processes. Records every invocation and returns
/// the canned response registered for the matching (exe, args) tuple.
///
/// Matching uses a custom routing function so callers can choose how
/// strict to be — e.g. "match by exe + first arg" for prefix dispatch.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<RecordedInvocation> _invocations = [];
    private readonly List<Responder> _responders = [];

    public IReadOnlyList<RecordedInvocation> Invocations => _invocations;

    /// <summary>
    /// Register a responder that returns <paramref name="response"/> when
    /// <paramref name="match"/> evaluates to true for an invocation.
    /// Responders are checked in registration order; the first match wins.
    /// </summary>
    public void When(Func<string, IReadOnlyList<string>, bool> match, ProcessResult response)
        => _responders.Add(new Responder(match, (_, _) => Task.FromResult(response)));

    /// <summary>
    /// Register an async responder. The handler receives the linked
    /// cancellation token (caller's CT linked with the per-attempt timeout
    /// CTS in <see cref="GhClient"/>); honoring it is how tests simulate
    /// timeout/cancellation behavior. Throw or return as appropriate.
    /// </summary>
    public void WhenAsync(
        Func<string, IReadOnlyList<string>, bool> match,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> handler)
        => _responders.Add(new Responder(match, handler));

    /// <summary>Convenience: match by exact executable name and full argument sequence.</summary>
    public void WhenExact(string exe, IReadOnlyList<string> args, ProcessResult response)
        => When(
            (e, a) => e == exe && a.SequenceEqual(args, StringComparer.Ordinal),
            response);

    /// <summary>Convenience: match when exe matches and the supplied first args appear in order.</summary>
    public void WhenStartsWith(string exe, IReadOnlyList<string> argsPrefix, ProcessResult response)
        => When(
            (e, a) => e == exe
                && a.Count >= argsPrefix.Count
                && a.Take(argsPrefix.Count).SequenceEqual(argsPrefix, StringComparer.Ordinal),
            response);

    /// <summary>
    /// Convenience: register a sequence of responses for matching invocations.
    /// The Nth matching call returns the Nth response; if more calls arrive
    /// than responses, the last response is reused.
    /// </summary>
    public void WhenStartsWithSequence(string exe, IReadOnlyList<string> argsPrefix, params ProcessResult[] responses)
    {
        if (responses.Length == 0)
        {
            throw new ArgumentException("Must provide at least one response.", nameof(responses));
        }
        int call = -1;
        WhenAsync(
            (e, a) => e == exe
                && a.Count >= argsPrefix.Count
                && a.Take(argsPrefix.Count).SequenceEqual(argsPrefix, StringComparer.Ordinal),
            (_, _) =>
            {
                var idx = Math.Min(Interlocked.Increment(ref call), responses.Length - 1);
                return Task.FromResult(responses[idx]);
            });
    }

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default,
        string? workingDirectory = null,
        string? stdin = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        _invocations.Add(new RecordedInvocation(executable, arguments.ToArray(), workingDirectory, stdin, environment));

        foreach (var responder in _responders)
        {
            if (responder.Match(executable, arguments))
            {
                return responder.Handler(arguments, ct);
            }
        }

        throw new InvalidOperationException(
            $"FakeProcessRunner has no responder for: {executable} {string.Join(' ', arguments)}");
    }

    public sealed record RecordedInvocation(
        string Executable,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory,
        string? Stdin = null,
        IReadOnlyDictionary<string, string?>? Environment = null);

    private sealed record Responder(
        Func<string, IReadOnlyList<string>, bool> Match,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> Handler);
}
