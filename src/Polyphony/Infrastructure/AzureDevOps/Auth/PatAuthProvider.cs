using System.Text;

namespace Polyphony.Infrastructure.AzureDevOps.Auth;

/// <summary>
/// Implements <see cref="IPolyphonyAuthProvider"/> via Personal Access Token.
/// Wraps the existing <see cref="AdoTokenResolver"/> for env-var precedence
/// (<c>AZURE_DEVOPS_EXT_PAT</c> → <c>AZURE_DEVOPS_PAT</c> →
/// <c>SYSTEM_ACCESSTOKEN</c>) so a PAT-based CI environment continues to
/// work unchanged.
///
/// <para>
/// Returns a complete <c>"Basic base64(:pat)"</c> header value. PAT tokens
/// are stateless from polyphony's perspective — <see cref="InvalidateToken"/>
/// is a no-op (the resolver re-reads env on every call, so a rotated PAT is
/// picked up automatically).
/// </para>
///
/// <para>
/// Methodology ported from twig's <c>PatAuthProvider</c>; polyphony uses its
/// own env-var precedence list (matching the <c>az devops</c> CLI vocabulary).
/// </para>
/// </summary>
public sealed class PatAuthProvider : IPolyphonyAuthProvider
{
    private readonly AdoTokenResolver _resolver;

    public PatAuthProvider(AdoTokenResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    /// <remarks>PAT tokens are stateless — no cached state to clear.</remarks>
    public void InvalidateToken() { }

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var pat = _resolver.Resolve();
        if (string.IsNullOrWhiteSpace(pat))
        {
            return Task.FromException<string>(new AdoAuthenticationException(
                "No PAT found. Set AZURE_DEVOPS_EXT_PAT, AZURE_DEVOPS_PAT, or SYSTEM_ACCESSTOKEN."));
        }

        return Task.FromResult(FormatBasicAuth(pat));
    }

    /// <summary>
    /// Formats a PAT as a Basic auth header value: <c>Basic base64(:PAT)</c>.
    /// Empty username + PAT-as-password is the canonical ADO form.
    /// </summary>
    public static string FormatBasicAuth(string pat)
    {
        var bytes = Encoding.UTF8.GetBytes($":{pat}");
        return $"Basic {Convert.ToBase64String(bytes)}";
    }
}
