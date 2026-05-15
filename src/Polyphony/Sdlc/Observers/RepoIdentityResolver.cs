using System.Text.RegularExpressions;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Sdlc.Observers;

/// <summary>
/// Resolves the active <see cref="RepoIdentity"/> from operator-supplied
/// override flags first, falling back to <c>git remote get-url origin</c>
/// parsing when no overrides are present. Single source of truth for "what
/// repo are we operating against?" — replaces the six duplicated GitHub-only
/// regexes that used to live inside the observers and verbs.
///
/// <para>
/// <b>Resolution order:</b>
/// <list type="number">
///   <item>If the caller supplies a <c>platform</c> override, the matching
///     required fields must be non-empty:
///     <list type="bullet">
///       <item><c>platform=github</c> requires <c>repository</c> in
///         <c>"owner/name"</c> form (the gh-CLI slug).</item>
///       <item><c>platform=ado</c> requires <c>organization</c>,
///         <c>project</c>, and <c>repository</c> all non-empty.</item>
///     </list>
///     Missing fields produce a structured
///     <see cref="ResolvedRepoIdentity.Error"/> — the resolver does NOT fall
///     back to origin parsing once an override is supplied. Otherwise the
///     operator's intent could be silently ignored.</item>
///   <item>If no <c>platform</c> override (empty string), read
///     <c>git remote get-url origin</c> and try
///     <see cref="TryParseRemoteUrl"/>. Any parser hit wins.</item>
///   <item>If parsing fails, return a structured
///     <see cref="ResolvedRepoIdentity.Error"/> describing the URL that did
///     not match any known pattern.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Origin URL patterns.</b> The parser mirrors
/// <c>scripts/Invoke-PolyphonySdlc.ps1</c>'s detection (verified by
/// <c>scripts/Invoke-PolyphonySdlc.Tests.ps1:434-462</c>) so the launcher
/// and the verbs converge on the same identity from the same URL:
/// <list type="bullet">
///   <item><c>https://github.com/{owner}/{name}[.git][/]</c></item>
///   <item><c>git@github.com:{owner}/{name}[.git]</c></item>
///   <item><c>https://[user[:pat]@]dev.azure.com/{org}/{project}/_git/{repo}</c></item>
///   <item><c>git@ssh.dev.azure.com:v3/{org}/{project}/{repo}</c></item>
///   <item><c>https://{org}.visualstudio.com/{project}/_git/{repo}</c> (legacy)</item>
/// </list>
/// Path segments are URL-decoded (project / repo names with spaces are
/// common on ADO). A trailing <c>.git</c> and trailing slash are tolerated.
/// </para>
/// </summary>
public sealed class RepoIdentityResolver(IGitClient git)
{
    // ── Origin URL patterns ──────────────────────────────────────────────
    //
    // GitHub HTTP(S):     https://[user@]github.com/{owner}/{name}[.git][/]
    // GitHub SSH alias:   git@github.com:{owner}/{name}[.git]
    // GitHub SSH proper:  ssh://git@github.com/{owner}/{name}[.git]
    private static readonly Regex GitHubRegex = new(
        @"^(?:https?://(?:[^@/]+@)?|git@|ssh://(?:[^@/]+@)?)github\.com[:/]+([^/]+)/([^/]+?)(?:\.git)?/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ADO HTTP(S):        https://[user[:pat]@]dev.azure.com/{org}/{project}/_git/{repo}[/]
    private static readonly Regex AdoHttpRegex = new(
        @"^https?://(?:[^@/]+@)?dev\.azure\.com/([^/]+)/([^/]+)/_git/([^/]+?)/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ADO SSH:            git@ssh.dev.azure.com:v3/{org}/{project}/{repo}
    //                or:  ssh://git@ssh.dev.azure.com/v3/{org}/{project}/{repo}
    private static readonly Regex AdoSshRegex = new(
        @"^(?:git@ssh\.dev\.azure\.com:|ssh://(?:[^@/]+@)?ssh\.dev\.azure\.com/)v3/([^/]+)/([^/]+)/([^/]+?)/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Legacy ADO:         https://{org}.visualstudio.com/{project}/_git/{repo}[/]
    private static readonly Regex VstsRegex = new(
        @"^https?://(?:[^@/]+@)?([^./]+)\.visualstudio\.com/([^/]+)/_git/([^/]+?)/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // GitHub slug shape consumed by --repository on platform=github.
    private static readonly Regex GitHubSlugRegex = new(
        @"^([^/\s]+)/([^/\s]+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Resolve the active <see cref="RepoIdentity"/>, honouring operator
    /// overrides first and falling back to origin URL parsing.
    /// </summary>
    /// <param name="platform">Empty for auto-detect; <c>"github"</c> or <c>"ado"</c> to force.</param>
    /// <param name="organization">ADO organization. Required when <paramref name="platform"/> is <c>"ado"</c>.</param>
    /// <param name="project">ADO project. Required when <paramref name="platform"/> is <c>"ado"</c>.</param>
    /// <param name="repository">For ADO: repository name/GUID. For GitHub: <c>"owner/name"</c> slug. Required when <paramref name="platform"/> is non-empty.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ResolvedRepoIdentity> ResolveAsync(
        string platform,
        string organization,
        string project,
        string repository,
        CancellationToken ct = default)
    {
        // ── 1. Honor explicit platform overrides. ────────────────────────
        if (!string.IsNullOrWhiteSpace(platform))
        {
            return ResolveFromOverride(platform, organization, project, repository);
        }

        // ── 1b. Back-compat: bare --repository in 'owner/name' shape ─────
        // implies platform=github (callers that only pass --repo without a
        // platform override are pre-ADO-era — preserve their wiring).
        if (!string.IsNullOrWhiteSpace(repository))
        {
            var bareSlug = GitHubSlugRegex.Match(repository.Trim());
            if (bareSlug.Success)
            {
                return ResolvedRepoIdentity.WithIdentity(
                    new RepoIdentity.GitHubRepo(bareSlug.Groups[1].Value, bareSlug.Groups[2].Value));
            }
        }

        // ── 2. Fall back to origin URL parsing. ──────────────────────────
        string? url;
        try
        {
            url = await git.GetRemoteUrlAsync("origin", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ResolvedRepoIdentity.WithError(
                $"git remote get-url origin failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return ResolvedRepoIdentity.WithError(
                "no origin remote configured (git remote get-url origin returned empty)");
        }

        var parsed = TryParseRemoteUrl(url);
        return parsed is null
            ? ResolvedRepoIdentity.WithError(
                $"origin URL '{url}' did not match any known platform pattern (github.com, dev.azure.com, *.visualstudio.com)")
            : ResolvedRepoIdentity.WithIdentity(parsed);
    }

    /// <summary>
    /// Parse a single remote URL into a <see cref="RepoIdentity"/>. Returns
    /// null when no platform pattern matched. Pure (no I/O); the resolver
    /// uses this against the origin URL but it's exposed for tests and for
    /// callers that already have the URL in hand.
    /// </summary>
    public static RepoIdentity? TryParseRemoteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // Try GitHub (HTTP, SSH-alias, SSH-proper).
        var ghMatch = GitHubRegex.Match(url);
        if (ghMatch.Success)
        {
            var owner = Decode(ghMatch.Groups[1].Value);
            var name = Decode(ghMatch.Groups[2].Value);
            if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(name))
            {
                return new RepoIdentity.GitHubRepo(owner, name);
            }
        }

        // Try ADO HTTPS.
        var adoMatch = AdoHttpRegex.Match(url);
        if (adoMatch.Success)
        {
            return new RepoIdentity.AdoRepo(
                Decode(adoMatch.Groups[1].Value),
                Decode(adoMatch.Groups[2].Value),
                Decode(adoMatch.Groups[3].Value));
        }

        // Try ADO SSH.
        var adoSshMatch = AdoSshRegex.Match(url);
        if (adoSshMatch.Success)
        {
            return new RepoIdentity.AdoRepo(
                Decode(adoSshMatch.Groups[1].Value),
                Decode(adoSshMatch.Groups[2].Value),
                Decode(adoSshMatch.Groups[3].Value));
        }

        // Try legacy *.visualstudio.com (org-from-subdomain). The REST host is
        // dev.azure.com/{org}/... so we normalise to AdoRepo with the
        // subdomain-derived organization.
        var vstsMatch = VstsRegex.Match(url);
        if (vstsMatch.Success)
        {
            return new RepoIdentity.AdoRepo(
                Decode(vstsMatch.Groups[1].Value),
                Decode(vstsMatch.Groups[2].Value),
                Decode(vstsMatch.Groups[3].Value));
        }

        return null;
    }

    private static ResolvedRepoIdentity ResolveFromOverride(
        string platform, string organization, string project, string repository)
    {
        var normalized = platform.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "github":
                if (string.IsNullOrWhiteSpace(repository))
                {
                    return ResolvedRepoIdentity.WithError(
                        "platform=github requires --repository in 'owner/name' form");
                }
                var slugMatch = GitHubSlugRegex.Match(repository.Trim());
                if (!slugMatch.Success)
                {
                    return ResolvedRepoIdentity.WithError(
                        $"platform=github expected --repository as 'owner/name' (got '{repository}')");
                }
                return ResolvedRepoIdentity.WithIdentity(
                    new RepoIdentity.GitHubRepo(slugMatch.Groups[1].Value, slugMatch.Groups[2].Value));

            case "ado":
                var missing = new List<string>(3);
                if (string.IsNullOrWhiteSpace(organization)) missing.Add("--organization");
                if (string.IsNullOrWhiteSpace(project)) missing.Add("--project");
                if (string.IsNullOrWhiteSpace(repository)) missing.Add("--repository");
                if (missing.Count > 0)
                {
                    return ResolvedRepoIdentity.WithError(
                        $"platform=ado requires {string.Join(", ", missing)} (all non-empty)");
                }
                return ResolvedRepoIdentity.WithIdentity(
                    new RepoIdentity.AdoRepo(organization.Trim(), project.Trim(), repository.Trim()));

            default:
                return ResolvedRepoIdentity.WithError(
                    $"unknown --platform '{platform}' (expected 'github' or 'ado')");
        }
    }

    private static string Decode(string segment)
    {
        try
        {
            return Uri.UnescapeDataString(segment);
        }
        catch
        {
            return segment;
        }
    }
}

/// <summary>
/// Outcome of <see cref="RepoIdentityResolver.ResolveAsync"/>. Mirrors the
/// <c>(value, error)</c> envelope used by
/// <see cref="Polyphony.Manifest.ResolvedManifestPath"/> so the consuming
/// verbs follow the same routing-style "emit error on failure, emit identity
/// on success" pattern without throwing.
/// </summary>
public readonly record struct ResolvedRepoIdentity(RepoIdentity? Identity, string? Error)
{
    public static ResolvedRepoIdentity WithIdentity(RepoIdentity identity)
        => new(identity, Error: null);

    public static ResolvedRepoIdentity WithError(string error)
        => new(Identity: null, error);
}
