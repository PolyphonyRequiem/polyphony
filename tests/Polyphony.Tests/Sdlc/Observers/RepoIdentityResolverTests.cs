using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc.Observers;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Sdlc.Observers;

/// <summary>
/// Unit tests for <see cref="RepoIdentityResolver"/>:
/// <list type="bullet">
///   <item>Override path: github / ado happy paths + missing-field errors.</item>
///   <item>Origin URL parsing: every documented shape (HTTP, HTTPS, SSH,
///     legacy <c>*.visualstudio.com</c>, with/without auth prefix,
///     with/without trailing <c>.git</c> and trailing slash).</item>
///   <item>Origin failure shapes: missing remote, git error, unrecognised URL.</item>
/// </list>
/// </summary>
public sealed class RepoIdentityResolverTests
{
    // ── Override resolution ──────────────────────────────────────────────

    [Fact]
    public async Task Override_GitHubWithSlug_ReturnsGitHubRepo()
    {
        var resolver = CreateResolver(out _);

        var result = await resolver.ResolveAsync(
            platform: "github", organization: "", project: "", repository: "acme/widget",
            CancellationToken.None);

        result.Error.ShouldBeNull();
        var gh = result.Identity.ShouldBeOfType<RepoIdentity.GitHubRepo>();
        gh.Owner.ShouldBe("acme");
        gh.Name.ShouldBe("widget");
        gh.Slug.ShouldBe("acme/widget");
    }

    [Fact]
    public async Task Override_GitHubWithoutRepository_ReturnsError()
    {
        var resolver = CreateResolver(out _);

        var result = await resolver.ResolveAsync("github", "", "", "", CancellationToken.None);

        result.Identity.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("platform=github requires --repository");
    }

    [Fact]
    public async Task Override_GitHubWithMalformedRepository_ReturnsError()
    {
        var resolver = CreateResolver(out _);

        var result = await resolver.ResolveAsync("github", "", "", "not-a-slug", CancellationToken.None);

        result.Identity.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("'owner/name'");
    }

    [Fact]
    public async Task Override_AdoWithAllFields_ReturnsAdoRepo()
    {
        var resolver = CreateResolver(out _);

        var result = await resolver.ResolveAsync(
            platform: "ado", organization: "microsoft", project: "CloudVault",
            repository: "cloudvault-service-api", CancellationToken.None);

        result.Error.ShouldBeNull();
        var ado = result.Identity.ShouldBeOfType<RepoIdentity.AdoRepo>();
        ado.Organization.ShouldBe("microsoft");
        ado.Project.ShouldBe("CloudVault");
        ado.Repository.ShouldBe("cloudvault-service-api");
    }

    [Fact]
    public async Task Override_AdoMissingOrg_ReturnsErrorListingMissingFields()
    {
        var resolver = CreateResolver(out _);

        var result = await resolver.ResolveAsync(
            platform: "ado", organization: "", project: "p", repository: "r", CancellationToken.None);

        result.Identity.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("--organization");
    }

    [Fact]
    public async Task Override_AdoMissingAllFields_ListsAllMissing()
    {
        var resolver = CreateResolver(out _);

        var result = await resolver.ResolveAsync("ado", "", "", "", CancellationToken.None);

        result.Identity.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("--organization");
        result.Error.ShouldContain("--project");
        result.Error.ShouldContain("--repository");
    }

    [Fact]
    public async Task Override_UnknownPlatform_ReturnsError()
    {
        var resolver = CreateResolver(out _);

        var result = await resolver.ResolveAsync("gitlab", "", "", "owner/repo", CancellationToken.None);

        result.Identity.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("unknown --platform 'gitlab'");
    }

    [Fact]
    public async Task Override_PlatformIsCaseInsensitive()
    {
        var resolver = CreateResolver(out _);

        var result = await resolver.ResolveAsync("GitHub", "", "", "acme/widget", CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Identity.ShouldBeOfType<RepoIdentity.GitHubRepo>();
    }

    [Fact]
    public async Task Override_DoesNotInvokeGit()
    {
        var resolver = CreateResolver(out var runner);

        await resolver.ResolveAsync("github", "", "", "acme/widget", CancellationToken.None);

        // Resolver MUST NOT consult origin once an override is supplied.
        runner.Invocations.ShouldBeEmpty();
    }

    // ── Origin URL fallback ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/acme/widget.git")]
    [InlineData("https://github.com/acme/widget")]
    [InlineData("https://github.com/acme/widget/")]
    [InlineData("https://github.com/acme/widget.git/")]
    [InlineData("git@github.com:acme/widget.git")]
    [InlineData("git@github.com:acme/widget")]
    [InlineData("ssh://git@github.com/acme/widget.git")]
    [InlineData("https://oauth2:token@github.com/acme/widget.git")]
    public async Task Origin_GitHubVariants_ParseToGitHubRepo(string url)
    {
        var resolver = CreateResolver(out var runner);
        StubRemoteUrl(runner, url);

        var result = await resolver.ResolveAsync("", "", "", "", CancellationToken.None);

        result.Error.ShouldBeNull();
        var gh = result.Identity.ShouldBeOfType<RepoIdentity.GitHubRepo>();
        gh.Owner.ShouldBe("acme");
        gh.Name.ShouldBe("widget");
    }

    [Theory]
    [InlineData("https://dev.azure.com/microsoft/CloudVault/_git/cloudvault-service-api")]
    [InlineData("https://dev.azure.com/microsoft/CloudVault/_git/cloudvault-service-api/")]
    [InlineData("https://dangreen@dev.azure.com/microsoft/CloudVault/_git/cloudvault-service-api")]
    [InlineData("https://dangreen:pat@dev.azure.com/microsoft/CloudVault/_git/cloudvault-service-api")]
    public async Task Origin_AdoHttpsVariants_ParseToAdoRepo(string url)
    {
        var resolver = CreateResolver(out var runner);
        StubRemoteUrl(runner, url);

        var result = await resolver.ResolveAsync("", "", "", "", CancellationToken.None);

        result.Error.ShouldBeNull();
        var ado = result.Identity.ShouldBeOfType<RepoIdentity.AdoRepo>();
        ado.Organization.ShouldBe("microsoft");
        ado.Project.ShouldBe("CloudVault");
        ado.Repository.ShouldBe("cloudvault-service-api");
    }

    [Theory]
    [InlineData("git@ssh.dev.azure.com:v3/microsoft/CloudVault/cloudvault-service-api")]
    [InlineData("ssh://git@ssh.dev.azure.com/v3/microsoft/CloudVault/cloudvault-service-api")]
    public async Task Origin_AdoSshVariants_ParseToAdoRepo(string url)
    {
        var resolver = CreateResolver(out var runner);
        StubRemoteUrl(runner, url);

        var result = await resolver.ResolveAsync("", "", "", "", CancellationToken.None);

        result.Error.ShouldBeNull();
        var ado = result.Identity.ShouldBeOfType<RepoIdentity.AdoRepo>();
        ado.Organization.ShouldBe("microsoft");
        ado.Project.ShouldBe("CloudVault");
        ado.Repository.ShouldBe("cloudvault-service-api");
    }

    [Fact]
    public async Task Origin_LegacyVisualStudioCom_ParsesOrgFromSubdomain()
    {
        var resolver = CreateResolver(out var runner);
        StubRemoteUrl(runner, "https://microsoft.visualstudio.com/CloudVault/_git/cloudvault-service-api");

        var result = await resolver.ResolveAsync("", "", "", "", CancellationToken.None);

        result.Error.ShouldBeNull();
        var ado = result.Identity.ShouldBeOfType<RepoIdentity.AdoRepo>();
        ado.Organization.ShouldBe("microsoft");
        ado.Project.ShouldBe("CloudVault");
        ado.Repository.ShouldBe("cloudvault-service-api");
    }

    [Fact]
    public async Task Origin_UrlEncodedSegments_AreDecoded()
    {
        var resolver = CreateResolver(out var runner);
        // ADO project names with spaces are URL-encoded by git remote.
        StubRemoteUrl(runner, "https://dev.azure.com/microsoft/Cloud%20Vault/_git/service%20api");

        var result = await resolver.ResolveAsync("", "", "", "", CancellationToken.None);

        result.Error.ShouldBeNull();
        var ado = result.Identity.ShouldBeOfType<RepoIdentity.AdoRepo>();
        ado.Project.ShouldBe("Cloud Vault");
        ado.Repository.ShouldBe("service api");
    }

    [Fact]
    public async Task Origin_UnrecognisedUrl_ReturnsError()
    {
        var resolver = CreateResolver(out var runner);
        StubRemoteUrl(runner, "https://gitlab.com/acme/widget.git");

        var result = await resolver.ResolveAsync("", "", "", "", CancellationToken.None);

        result.Identity.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("did not match any known platform pattern");
    }

    [Fact]
    public async Task Origin_MissingRemote_ReturnsError()
    {
        var resolver = CreateResolver(out var runner);
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, "", ""));

        var result = await resolver.ResolveAsync("", "", "", "", CancellationToken.None);

        result.Identity.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("no origin remote");
    }

    // ── Static URL parser ─────────────────────────────────────────────────

    [Fact]
    public void TryParseRemoteUrl_NullOrWhitespace_ReturnsNull()
    {
        RepoIdentityResolver.TryParseRemoteUrl(null!).ShouldBeNull();
        RepoIdentityResolver.TryParseRemoteUrl("").ShouldBeNull();
        RepoIdentityResolver.TryParseRemoteUrl("   ").ShouldBeNull();
    }

    [Fact]
    public void TryParseRemoteUrl_GitHubBasic_ReturnsGitHubRepo()
    {
        var result = RepoIdentityResolver.TryParseRemoteUrl("https://github.com/acme/widget.git");
        var gh = result.ShouldBeOfType<RepoIdentity.GitHubRepo>();
        gh.Owner.ShouldBe("acme");
        gh.Name.ShouldBe("widget");
    }

    [Fact]
    public void TryParseRemoteUrl_AdoBasic_ReturnsAdoRepo()
    {
        var result = RepoIdentityResolver.TryParseRemoteUrl(
            "https://dev.azure.com/microsoft/CloudVault/_git/cloudvault-service-api");
        var ado = result.ShouldBeOfType<RepoIdentity.AdoRepo>();
        ado.Organization.ShouldBe("microsoft");
    }

    // ── DisplayString ─────────────────────────────────────────────────────

    [Fact]
    public void DisplayString_GitHub_RendersAsGithubDotComForm()
    {
        var id = new RepoIdentity.GitHubRepo("acme", "widget");
        id.DisplayString().ShouldBe("github.com/acme/widget");
    }

    [Fact]
    public void DisplayString_Ado_RendersAsDevAzureComGitForm()
    {
        var id = new RepoIdentity.AdoRepo("microsoft", "CloudVault", "cloudvault-service-api");
        id.DisplayString().ShouldBe("dev.azure.com/microsoft/CloudVault/_git/cloudvault-service-api");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static RepoIdentityResolver CreateResolver(out FakeProcessRunner runner)
    {
        runner = new FakeProcessRunner();
        return new RepoIdentityResolver(new GitClient(runner));
    }

    private static void StubRemoteUrl(FakeProcessRunner runner, string url)
        => runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, url + "\n", ""));
}
