namespace Polyphony.Infrastructure.AzureDevOps.Auth;

/// <summary>
/// Raised when an Azure DevOps access token cannot be acquired by any
/// configured <see cref="IPolyphonyAuthProvider"/>. Surfaces actionable
/// guidance in the message: which credential paths were tried, and what
/// the operator can do to recover (set a PAT env var, run <c>az login</c>,
/// or clear the local refresh-token store).
///
/// <para>
/// Distinct from <see cref="InvalidOperationException"/> so call-site catch
/// blocks can react to "no credentials" without conflating it with the
/// generic "invalid state" envelope. Existing PR-verb error catches keep
/// the <see cref="InvalidOperationException"/> branch as defense-in-depth
/// for older code paths during the transition.
/// </para>
/// </summary>
public sealed class AdoAuthenticationException : Exception
{
    /// <summary>
    /// Creates a new <see cref="AdoAuthenticationException"/> with the
    /// supplied operator-facing message.
    /// </summary>
    public AdoAuthenticationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a new <see cref="AdoAuthenticationException"/> with the
    /// supplied operator-facing message and an inner cause.
    /// </summary>
    public AdoAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
