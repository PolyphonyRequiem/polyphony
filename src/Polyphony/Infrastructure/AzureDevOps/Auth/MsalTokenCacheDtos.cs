using System.Text.Json.Serialization;

namespace Polyphony.Infrastructure.AzureDevOps.Auth;

/// <summary>
/// MSAL token cache entry for a single access token.
/// </summary>
public sealed class MsalAccessTokenEntry
{
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("expires_on")]
    public string? ExpiresOn { get; set; }
}

/// <summary>
/// MSAL token cache entry for a refresh token.
/// </summary>
public sealed class MsalRefreshTokenEntry
{
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("home_account_id")]
    public string? HomeAccountId { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }
}

/// <summary>
/// MSAL token cache entry for an account.
/// </summary>
public sealed class MsalAccountEntry
{
    [JsonPropertyName("home_account_id")]
    public string? HomeAccountId { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("realm")]
    public string? Realm { get; set; }
}

/// <summary>
/// OAuth2 token endpoint response (subset of fields polyphony uses).
/// </summary>
public sealed class TokenRefreshResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    /// <summary>
    /// AAD rotates refresh tokens on every successful exchange. Capturing this and
    /// writing it back to the store is what keeps the 90-day sliding inactivity
    /// window sliding — without this, the stored RT slowly ages out even for
    /// active users.
    /// </summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

/// <summary>
/// Root DTO for the MSAL token cache JSON file written by Azure CLI at
/// <c>~/.azure/msal_token_cache.json</c>. Top-level keys are PascalCase
/// (<c>AccessToken</c>, <c>RefreshToken</c>, <c>Account</c>) per MSAL's
/// own format — these explicit <see cref="JsonPropertyName"/> attrs are
/// load-bearing since polyphony's source-gen default is snake_case.
/// </summary>
public sealed class MsalTokenCache
{
    [JsonPropertyName("AccessToken")]
    public Dictionary<string, MsalAccessTokenEntry>? AccessToken { get; set; }

    [JsonPropertyName("RefreshToken")]
    public Dictionary<string, MsalRefreshTokenEntry>? RefreshToken { get; set; }

    [JsonPropertyName("Account")]
    public Dictionary<string, MsalAccountEntry>? Account { get; set; }
}
