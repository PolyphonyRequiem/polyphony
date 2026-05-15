namespace Polyphony.Infrastructure.AzureDevOps.Auth;

/// <summary>
/// Provides a complete <c>Authorization</c> header value for Azure DevOps
/// REST calls. Implementations may return either a <c>Basic</c> form (PAT)
/// or a raw JWT (which the HTTP layer wraps as <c>Bearer</c>).
///
/// <para>
/// Detection is shape-based: a returned string starting with <c>"Basic "</c>
/// is sent verbatim; anything else is treated as an opaque bearer token.
/// This mirrors twig's two-provider model without requiring callers to
/// thread a per-provider scheme flag.
/// </para>
///
/// <para>
/// <see cref="InvalidateToken"/> drops any cached token so the next
/// <see cref="GetAccessTokenAsync"/> call acquires a fresh one. Call this
/// from the HTTP layer when the server returns a hard 401 / sign-in HTML
/// challenge — not for transient 5xx, where the token is still good.
/// </para>
/// </summary>
public interface IPolyphonyAuthProvider
{
    /// <summary>
    /// Returns a complete Authorization header value
    /// (e.g. <c>"Basic …"</c> or a raw JWT). Throws
    /// <see cref="AdoAuthenticationException"/> when no credential is
    /// available across all configured paths.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops any cached token so the next
    /// <see cref="GetAccessTokenAsync"/> call re-acquires from source.
    /// </summary>
    void InvalidateToken();
}
