namespace Polyphony.Infrastructure.AzureDevOps.Auth;

/// <summary>
/// Composes <see cref="PatAuthProvider"/> and <see cref="AdoAccessTokenProvider"/>:
/// PAT env vars (delegated to the wrapped <see cref="AdoTokenResolver"/>)
/// take precedence on every call so a rotated PAT is picked up without
/// process restart; when no PAT is set, the call falls through to the AAD
/// MSAL chain.
///
/// <para>
/// This is the production composition. Tests can construct either provider
/// directly to exercise a single path.
/// </para>
///
/// <para>
/// Methodology ported from twig's <c>AuthProviderFactory.Create</c>; twig
/// selects the provider once at startup based on a config field, polyphony
/// auto-selects per-call so a CI environment doesn't need any config to
/// favour PAT over AAD.
/// </para>
/// </summary>
public sealed class CompositeAdoAuthProvider : IPolyphonyAuthProvider
{
    private readonly AdoTokenResolver _patResolver;
    private readonly IPolyphonyAuthProvider _aadProvider;

    public CompositeAdoAuthProvider(AdoTokenResolver patResolver, IPolyphonyAuthProvider aadProvider)
    {
        _patResolver = patResolver ?? throw new ArgumentNullException(nameof(patResolver));
        _aadProvider = aadProvider ?? throw new ArgumentNullException(nameof(aadProvider));
    }

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var pat = _patResolver.Resolve();
        if (!string.IsNullOrWhiteSpace(pat))
            return Task.FromResult(PatAuthProvider.FormatBasicAuth(pat));

        return _aadProvider.GetAccessTokenAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// PAT path is stateless; only the AAD chain holds cache state worth
    /// invalidating. A 401 may stem from either path — we route the
    /// invalidation to the AAD provider since that's the only side that
    /// can act on it.
    /// </remarks>
    public void InvalidateToken() => _aadProvider.InvalidateToken();
}

/// <summary>
/// Centralised construction for the production auth chain. Mirrors twig's
/// <c>AuthProviderFactory</c> pattern so every entry point produces the
/// same provider composition.
/// </summary>
public static class PolyphonyAdoAuthFactory
{
    /// <summary>
    /// Creates the production composite: env-var PAT precedence, AAD MSAL
    /// fallback. Both legs are constructed eagerly; the AAD leg's caches are
    /// lazy (no I/O until the first call).
    /// </summary>
    public static IPolyphonyAuthProvider CreateForAdo(AdoTokenResolver tokenResolver)
    {
        ArgumentNullException.ThrowIfNull(tokenResolver);
        return new CompositeAdoAuthProvider(tokenResolver, new AdoAccessTokenProvider());
    }
}
