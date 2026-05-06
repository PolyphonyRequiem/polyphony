namespace Polyphony.Infrastructure.AzureDevOps;

/// <summary>
/// Typed wrapper over the Azure DevOps REST API. Phase 5 scaffolding —
/// currently exposes only the auth probe. Pull-request, review, and vote
/// operations will land in subsequent PRs and extend this interface.
///
/// <para>
/// This is polyphony's own ADO client (deliberately not a dependency on the
/// twig2 ADO infrastructure). The wire-level concerns (PAT resolution, Basic
/// auth header format, HTTP timeout/retry policy) borrow the established
/// pattern from <c>Twig.Infrastructure.Ado.AdoRestClient</c> but the verbs
/// will be polyphony-specific.
/// </para>
///
/// <para>
/// Failure semantics mirror <see cref="Polyphony.Infrastructure.Processes.IGhClient"/>:
/// every method enforces a timeout + retry policy (see
/// <see cref="AdoClientPolicy"/>) so a hung dev.azure.com call cannot stall
/// the orchestrator indefinitely. Probe-style methods report failure via the
/// result type rather than throwing, so preflight can degrade gracefully.
/// </para>
/// </summary>
public interface IAdoClient
{
    /// <summary>
    /// Probe the configured PAT against
    /// <c>https://dev.azure.com/_apis/connectionData?api-version=7.1</c>.
    ///
    /// <para>
    /// Best-effort: does not throw. Outcomes are encoded in
    /// <see cref="AdoAuthStatus"/>:
    /// <list type="bullet">
    ///   <item>200 + parseable body → <c>IsAuthenticated = true</c>;
    ///     <see cref="AdoAuthStatus.OrganizationName"/> is set to the
    ///     authenticated user's provider display name.</item>
    ///   <item>No PAT configured → <c>IsAuthenticated = false</c> with a
    ///     "no PAT configured" detail pointing at the env var and az fallback.</item>
    ///   <item>401 / 403 → <c>IsAuthenticated = false</c> with a
    ///     "PAT rejected" detail.</item>
    ///   <item>Timeout (every attempt exhausted) → <c>IsAuthenticated = false</c>
    ///     with a "timed out probing dev.azure.com" detail.</item>
    ///   <item>Any other error (network, malformed JSON, unexpected status)
    ///     → <c>IsAuthenticated = false</c> with a "Probe failed: ..."
    ///     detail.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="ct">Caller cancellation token; propagated immediately.</param>
    Task<AdoAuthStatus> GetAuthStatusAsync(CancellationToken ct = default);
}
