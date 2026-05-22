using Twig.Domain.Interfaces;

namespace Polyphony.Tests.TestFixtures;

public sealed class RepositoryServiceProvider(IWorkItemRepository repository) : IServiceProvider
{
    public object? GetService(Type serviceType)
        => serviceType == typeof(IWorkItemRepository) ? repository : null;
}
