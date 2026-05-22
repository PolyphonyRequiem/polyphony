using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;

namespace Polyphony.Journal.Reset.Deleters;

public sealed class GitHubPrDeleter(IGhClient gh) : IResourceDeleter
{
    private readonly IGhClient _gh = gh;

    public string Kind => ResourceKind.GitHubPr;

    public async Task<ResourceDeleteOutcome> DeleteAsync(
        ProjectedResourceState resource,
        ObservedResourceState? observation,
        ResourceDeletionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        var repoSlug = ResourceObserverSupport.GetStringAttribute(observation?.ActualAttributes, "repo_slug")
            ?? ResourceObserverSupport.GetStringAttribute(resource.Attributes, "repo_slug");
        var prNumber = ResourceObserverSupport.GetIntAttribute(observation?.ActualAttributes, "pr_number")
            ?? ResourceObserverSupport.GetIntAttribute(resource.Attributes, "pr_number");
        if (string.IsNullOrWhiteSpace(repoSlug)
            && ResourceObserverSupport.TryParseGitHubPullRequest(resource.Id, out var parsedSlug, out var parsedNumber))
        {
            repoSlug = parsedSlug;
            prNumber = parsedNumber;
        }

        if (string.IsNullOrWhiteSpace(repoSlug) || prNumber is null)
        {
            return new ResourceDeleteOutcome
            {
                Success = false,
                Error = $"Could not resolve GitHub PR identity from '{resource.Id}'.",
            };
        }

        var comment = string.IsNullOrWhiteSpace(context.Comment)
            ? ResetCommands.DefaultResetComment
            : context.Comment;
        var closed = await _gh.ClosePullRequestAsync(repoSlug, prNumber.Value, comment, ct).ConfigureAwait(false);
        return new ResourceDeleteOutcome
        {
            Success = closed,
            Deleted = closed,
            Error = closed ? null : $"GitHub refused to close PR #{prNumber.Value} in {repoSlug}.",
        };
    }
}
