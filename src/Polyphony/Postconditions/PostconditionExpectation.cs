namespace Polyphony.Postconditions;

/// <summary>
/// One row of the post-condition the caller wants verified on the remote:
/// at <see cref="Path"/> on the target branch, the blob's contents must
/// equal <see cref="ExpectedContent"/>.
///
/// <para>"Expected content" is whatever the caller has decided is the
/// authoritative bytes — typically the on-disk content the caller has
/// already committed to local HEAD. The verifier neither inspects HEAD
/// nor the worktree; it only compares <c>origin/{branch}:{path}</c> to
/// <see cref="ExpectedContent"/>.</para>
///
/// <para><see cref="Path"/> must address a single file (a blob), not a
/// tree. The verifier asks git for the file's content via
/// <c>git show {ref}:{path}</c>; passing a directory yields a
/// non-comparable result and the verifier will report it as missing.</para>
/// </summary>
public sealed record PostconditionExpectation(string Path, string ExpectedContent);
