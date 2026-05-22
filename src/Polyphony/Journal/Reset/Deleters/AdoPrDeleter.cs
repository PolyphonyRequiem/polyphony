using Polyphony.Commands;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;
using Polyphony.Sdlc.Observers;

namespace Polyphony.Journal.Reset.Deleters;

public sealed class AdoPrDeleter(IAdoClient ado) : IResourceDeleter
{
    private readonly IAdoClient _ado = ado;

    public string Kind => ResourceKind.AdoPr;

    public async Task<ResourceDeleteOutcome> DeleteAsync(
        ProjectedResourceState resource,
        ObservedResourceState? observation,
        ResourceDeletionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(context);

        var organization = ResourceObserverSupport.GetStringAttribute(observation?.ActualAttributes, "organization")
            ?? ResourceObserverSupport.GetStringAttribute(resource.Attributes, "organization");
        var project = ResourceObserverSupport.GetStringAttribute(observation?.ActualAttributes, "project")
            ?? ResourceObserverSupport.GetStringAttribute(resource.Attributes, "project");
        var repository = ResourceObserverSupport.GetStringAttribute(observation?.ActualAttributes, "repository")
            ?? ResourceObserverSupport.GetStringAttribute(resource.Attributes, "repository");
        var prNumber = ResourceObserverSupport.GetIntAttribute(observation?.ActualAttributes, "pr_number")
            ?? ResourceObserverSupport.GetIntAttribute(resource.Attributes, "pr_number");

        if ((string.IsNullOrWhiteSpace(organization)
             || string.IsNullOrWhiteSpace(project)
             || string.IsNullOrWhiteSpace(repository)
             || prNumber is null)
            && ResourceObserverSupport.TryParseAdoPullRequest(resource.Id, out var parsedRepo, out var parsedNumber))
        {
            organization = parsedRepo.Organization;
            project = parsedRepo.Project;
            repository = parsedRepo.Repository;
            prNumber = parsedNumber;
        }

        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(repository)
            || prNumber is null)
        {
            return new ResourceDeleteOutcome
            {
                Success = false,
                Error = $"Could not resolve ADO PR identity from '{resource.Id}'.",
            };
        }

        var comment = string.IsNullOrWhiteSpace(context.Comment)
            ? ResetCommands.DefaultResetComment
            : context.Comment;
        var closed = await _ado.ClosePullRequestAsync(
            organization,
            project,
            repository,
            prNumber.Value,
            comment,
            ct).ConfigureAwait(false);
        return new ResourceDeleteOutcome
        {
            Success = closed,
            Deleted = closed,
            Error = closed ? null : $"ADO refused to abandon PR #{prNumber.Value} in {organization}/{project}/{repository}.",
        };
    }
}
