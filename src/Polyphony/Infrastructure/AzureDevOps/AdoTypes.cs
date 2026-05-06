using System.Text.Json.Serialization;

namespace Polyphony.Infrastructure.AzureDevOps;

/// <summary>
/// Result of an Azure DevOps auth probe (see
/// <see cref="IAdoClient.GetAuthStatusAsync"/>).
///
/// <para>
/// Mirrors the shape of <see cref="Polyphony.Infrastructure.Processes.GhAuthStatus"/>:
/// best-effort, never throws. Probe failures (missing PAT, 401/403, timeout,
/// network error) all surface as <see cref="IsAuthenticated"/> = <c>false</c>
/// with a human-readable <see cref="Detail"/> so preflight can route to
/// remediation rather than crash.
/// </para>
/// </summary>
/// <param name="IsAuthenticated">
/// True when the configured PAT (or token) successfully resolved against the
/// ADO connection-data endpoint.
/// </param>
/// <param name="Detail">
/// Human-readable detail describing the probe outcome — used in CLI output and
/// preflight diagnostics. Never null; empty when the probe succeeded with no
/// extra context to report.
/// </param>
/// <param name="OrganizationName">
/// The ADO organization the probe was run against (or the authenticated
/// user's display name when the org is not explicitly scoped). Null when the
/// probe failed before contacting ADO or the response did not carry it.
/// </param>
public sealed record AdoAuthStatus(
    bool IsAuthenticated,
    string Detail,
    string? OrganizationName);

/// <summary>
/// DTO for the <c>connectionData</c> ADO REST response. Used by
/// <see cref="AdoClient.GetAuthStatusAsync"/> to extract the authenticated
/// user's display name.
///
/// <para>
/// AOT-safe: registered in <see cref="PolyphonyJsonContext"/> for source-gen
/// deserialization. ADO returns camelCase property names, so each member uses
/// <see cref="JsonPropertyNameAttribute"/> to override the context's
/// snake_case_lower naming policy.
/// </para>
/// </summary>
public sealed class AdoConnectionData
{
    /// <summary>The authenticated identity, when the PAT resolved successfully.</summary>
    [JsonPropertyName("authenticatedUser")]
    public AdoConnectionDataUser? AuthenticatedUser { get; set; }
}

/// <summary>
/// User node nested inside <see cref="AdoConnectionData"/>.
/// Only the display name is read; other fields are ignored.
/// </summary>
public sealed class AdoConnectionDataUser
{
    /// <summary>
    /// Display name surfaced by the identity provider (typically the user's
    /// AAD display name). Falls back to <c>customDisplayName</c> when the
    /// provider value is missing.
    /// </summary>
    [JsonPropertyName("providerDisplayName")]
    public string? ProviderDisplayName { get; set; }

    /// <summary>Custom override for the display name; rarely populated.</summary>
    [JsonPropertyName("customDisplayName")]
    public string? CustomDisplayName { get; set; }
}
