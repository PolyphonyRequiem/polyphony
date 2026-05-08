namespace Polyphony.Postconditions;

/// <summary>
/// Shared "is the post-condition met on origin?" check used by every verb
/// whose desired post-condition is "<c>origin/{branch}:{path}</c> contains
/// blob <i>X</i>". The abstraction exists because each commit-and-push verb
/// kept implementing this its own way and shipping the same Class B bug
/// (issues #177, #179, #192): the verb returns no-op when local HEAD looks
/// clean without checking that origin actually matches.
///
/// <para>Contract:</para>
/// <list type="number">
///   <item>Refresh the local view of <c>{remote}/{branch}</c> (best-effort
///     fetch — fetch failure is treated as "remote ref doesn't exist",
///     which is exactly the case we want to surface as
///     <see cref="PostconditionOutcome.NeedsPush"/>).</item>
///   <item>For each <see cref="PostconditionExpectation"/>, read
///     <c>{remote}/{branch}:{path}</c> via
///     <see cref="Polyphony.Infrastructure.Processes.IGitClient.ShowFileAtRefAsync"/>.</item>
///   <item>Classify into the three <see cref="PostconditionOutcome"/> cases.</item>
/// </list>
///
/// <para>The verifier deliberately does not push, commit, or mutate the
/// repository. It is an inspection. Callers (the commit-and-push verbs)
/// own the response.</para>
/// </summary>
public interface IPostconditionVerifier
{
    /// <summary>
    /// Inspects <c>{remote}/{branch}</c> and returns one of three
    /// <see cref="PostconditionOutcome"/> cases.
    /// </summary>
    /// <param name="branch">Branch name on the remote (without <c>refs/heads/</c>).</param>
    /// <param name="expectations">Paths and expected content. Empty input
    /// returns <see cref="PostconditionOutcome.Satisfied"/> trivially —
    /// "no expectations" is satisfied vacuously.</param>
    /// <param name="remote">Remote name (defaults to <c>origin</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PostconditionOutcome> VerifyAsync(
        string branch,
        IReadOnlyList<PostconditionExpectation> expectations,
        string remote = "origin",
        CancellationToken ct = default);
}
