using Polyphony.Infrastructure.Processes;

namespace Polyphony.Postconditions;

/// <summary>
/// Default <see cref="IPostconditionVerifier"/> backed by
/// <see cref="IGitClient"/>. Shells out to <c>git fetch</c> and
/// <c>git show</c>; no LibGit2 dependency, no new transport.
/// </summary>
public sealed class PostconditionVerifier(IGitClient git) : IPostconditionVerifier
{
    public async Task<PostconditionOutcome> VerifyAsync(
        string branch,
        IReadOnlyList<PostconditionExpectation> expectations,
        string remote = "origin",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(branch);
        ArgumentException.ThrowIfNullOrEmpty(remote);
        ArgumentNullException.ThrowIfNull(expectations);

        if (expectations.Count == 0)
        {
            // No expectations are vacuously satisfied. Avoid a needless
            // fetch — callers passing zero rows are usually doing it
            // defensively (e.g. computed-empty paths list).
            return new PostconditionOutcome.Satisfied();
        }

        // 1. Refresh the local view of {remote}/{branch}. The most common
        //    fetch failure is "remote ref doesn't exist yet" — first push
        //    of a fresh feature branch — which is the exact case that
        //    needs to surface as NeedsPush rather than Satisfied. Treat
        //    every fetch failure that way; if the remote is genuinely
        //    unreachable the subsequent push will fail loudly with the
        //    real diagnostic.
        try
        {
            await git.FetchAsync(remote, branch, ct).ConfigureAwait(false);
        }
        catch (ExternalToolException)
        {
            return new PostconditionOutcome.NeedsPush(
                expectations.Select(e => e.Path).ToList());
        }

        // 2. Compare each expected blob to whatever the remote has at the
        //    same path. Classify per row, then collapse into the strongest
        //    outcome (Conflict beats NeedsPush beats Satisfied).
        var refSpec = $"{remote}/{branch}";
        var missing = new List<string>();
        var conflicts = new List<PostconditionConflict>();

        foreach (var exp in expectations)
        {
            string? actual;
            try
            {
                actual = await git.ShowFileAtRefAsync(refSpec, exp.Path, ct).ConfigureAwait(false);
            }
            catch (ExternalToolException)
            {
                // Treat any unrecognized git failure on the read side as
                // "missing" — same defense as the fetch path. The push
                // that follows will surface the real error if the repo is
                // actually broken.
                actual = null;
            }

            if (actual is null)
            {
                missing.Add(exp.Path);
                continue;
            }

            if (!string.Equals(actual, exp.ExpectedContent, StringComparison.Ordinal))
            {
                conflicts.Add(new PostconditionConflict(exp.Path, exp.ExpectedContent, actual));
            }
        }

        if (conflicts.Count > 0)
        {
            return new PostconditionOutcome.Conflict(conflicts);
        }

        if (missing.Count > 0)
        {
            return new PostconditionOutcome.NeedsPush(missing);
        }

        return new PostconditionOutcome.Satisfied();
    }
}
