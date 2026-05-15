namespace Polyphony.Sdlc.Observers;

/// <summary>
/// Discriminated record carrying a repository's platform-qualified identity.
/// Replaces the GitHub-only <c>string slug</c> that used to flow through the
/// observation layer (<see cref="PlanObserver"/>) and through every verb that
/// derives the slug from <c>git remote get-url origin</c>
/// (<see cref="Polyphony.Commands.PlanCommands"/>,
/// <see cref="Polyphony.Commands.PrCommands"/>,
/// <see cref="Polyphony.Commands.BranchCommands"/>,
/// <see cref="Polyphony.Commands.StatusCommand"/>).
///
/// <para>
/// The two variants reflect the two clients polyphony already uses: the
/// <see cref="GitHubRepo"/> case feeds <see cref="Polyphony.Infrastructure.Processes.IGhClient"/>
/// (which takes a <c>string slug</c> like <c>"owner/name"</c>), and the
/// <see cref="AdoRepo"/> case feeds <see cref="Polyphony.Infrastructure.AzureDevOps.IAdoClient"/>
/// (which takes <c>(organization, project, repository)</c>).
/// </para>
///
/// <para>
/// The type is a sealed discriminated union: a private no-arg ctor on the
/// abstract base prevents external subclasses, and the two nested
/// <c>sealed record</c>s are the only inhabitants. Pattern-match exhaustively
/// at every consumer; do not add a default arm.
/// </para>
/// </summary>
public abstract record RepoIdentity
{
    private RepoIdentity() { }

    /// <summary>
    /// GitHub-hosted repository identified by <paramref name="Owner"/> and
    /// <paramref name="Name"/>. The combined <c>"{Owner}/{Name}"</c> form is
    /// the same string the <see cref="Polyphony.Infrastructure.Processes.IGhClient"/>
    /// methods accept as <c>string repoSlug</c>; use <see cref="Slug"/> to
    /// produce it.
    /// </summary>
    /// <param name="Owner">Repository owner (user or organization). Non-empty.</param>
    /// <param name="Name">Repository name. Non-empty. Does NOT include a trailing <c>.git</c>.</param>
    public sealed record GitHubRepo(string Owner, string Name) : RepoIdentity
    {
        /// <summary>
        /// The <c>"owner/name"</c> slug consumed by every
        /// <see cref="Polyphony.Infrastructure.Processes.IGhClient"/> method.
        /// </summary>
        public string Slug => $"{Owner}/{Name}";
    }

    /// <summary>
    /// Azure DevOps-hosted repository identified by
    /// <paramref name="Organization"/>, <paramref name="Project"/>, and
    /// <paramref name="Repository"/>. The triple matches the parameter shape
    /// of every <see cref="Polyphony.Infrastructure.AzureDevOps.IAdoClient"/>
    /// method.
    /// </summary>
    /// <param name="Organization">ADO organization (subdomain of <c>dev.azure.com</c>, or the legacy <c>{org}.visualstudio.com</c> subdomain). Non-empty.</param>
    /// <param name="Project">ADO project name. Non-empty.</param>
    /// <param name="Repository">ADO repository identifier (name OR GUID — both are accepted by the REST API). Non-empty.</param>
    public sealed record AdoRepo(string Organization, string Project, string Repository) : RepoIdentity;

    /// <summary>
    /// Render the identity as a human-readable, platform-qualified string for
    /// diagnostics and operator-facing messages. Stable shape across the
    /// codebase so log scrapers can rely on it:
    /// <list type="bullet">
    ///   <item><c>github.com/{owner}/{name}</c></item>
    ///   <item><c>dev.azure.com/{org}/{project}/_git/{repo}</c></item>
    /// </list>
    /// Distinct from <see cref="GitHubRepo.Slug"/> — that one is the
    /// gh-CLI-shaped <c>owner/name</c>; this one is the qualified URL-like
    /// form. Not a real URL (no scheme).
    /// </summary>
    public string DisplayString() => this switch
    {
        GitHubRepo gh => $"github.com/{gh.Owner}/{gh.Name}",
        AdoRepo ado => $"dev.azure.com/{ado.Organization}/{ado.Project}/_git/{ado.Repository}",
        _ => throw new InvalidOperationException("RepoIdentity is sealed; new variants must update DisplayString()."),
    };
}
