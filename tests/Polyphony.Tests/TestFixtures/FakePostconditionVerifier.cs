using Polyphony.Postconditions;

namespace Polyphony.Tests.TestFixtures;

/// <summary>
/// Test double for <see cref="IPostconditionVerifier"/>. Defaults to
/// returning <see cref="PostconditionOutcome.Satisfied"/> — fine for
/// unit tests of verbs whose code path never reaches the verifier.
///
/// <para>For tests that exercise the commit-and-push verifier branches,
/// configure <see cref="NextOutcome"/> with the desired outcome before
/// invoking the verb. Recorded calls are exposed via <see cref="Calls"/>
/// for assertions on what the consumer asked.</para>
///
/// <para>For higher-fidelity tests that want to exercise the real
/// fetch+show plumbing, construct
/// <see cref="PostconditionVerifier"/> directly against a
/// <see cref="Polyphony.Tests.Infrastructure.Processes.FakeProcessRunner"/>
/// instead.</para>
/// </summary>
public sealed class FakePostconditionVerifier : IPostconditionVerifier
{
    private readonly List<RecordedCall> calls = [];

    public IReadOnlyList<RecordedCall> Calls => this.calls;

    /// <summary>The outcome to return on the next (and subsequent) calls. Defaults to Satisfied.</summary>
    public PostconditionOutcome NextOutcome { get; set; } = new PostconditionOutcome.Satisfied();

    public Task<PostconditionOutcome> VerifyAsync(
        string branch,
        IReadOnlyList<PostconditionExpectation> expectations,
        string remote = "origin",
        CancellationToken ct = default)
    {
        this.calls.Add(new RecordedCall(branch, expectations.ToArray(), remote));
        return Task.FromResult(this.NextOutcome);
    }

    public sealed record RecordedCall(
        string Branch,
        IReadOnlyList<PostconditionExpectation> Expectations,
        string Remote);
}
