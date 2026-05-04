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
        => _responders.Add(new Responder(match, response));

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

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default,
        string? workingDirectory = null)
    {
        _invocations.Add(new RecordedInvocation(executable, arguments.ToArray(), workingDirectory));

        foreach (var responder in _responders)
        {
            if (responder.Match(executable, arguments))
            {
                return Task.FromResult(responder.Response);
            }
        }

        throw new InvalidOperationException(
            $"FakeProcessRunner has no responder for: {executable} {string.Join(' ', arguments)}");
    }

    public sealed record RecordedInvocation(
        string Executable,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory);

    private sealed record Responder(
        Func<string, IReadOnlyList<string>, bool> Match,
        ProcessResult Response);
}
