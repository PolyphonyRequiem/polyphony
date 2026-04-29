using Microsoft.Extensions.DependencyInjection;
using Polyphony.Configuration;
using Polyphony.Infrastructure;
using Polyphony.Tests.Configuration;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure;

public sealed class PolyphonyServiceRegistrationTests
{
    [Fact]
    public void AddPolyphonyServices_RegistersProcessConfig()
    {
        // Arrange
        var configPath = FindProcessConfigPath();
        if (configPath is null) return;

        var services = new ServiceCollection();

        // Act
        services.AddPolyphonyServices(configPath, twigDir: null);

        // Assert — ProcessConfig is registered
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ProcessConfig));
        descriptor.ShouldNotBeNull();
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddPolyphonyServices_RegistersTwigCoreServices()
    {
        // Arrange
        var configPath = FindProcessConfigPath();
        if (configPath is null) return;

        var services = new ServiceCollection();

        // Act
        services.AddPolyphonyServices(configPath, twigDir: null);

        // Assert — twig core services are registered (check by type name to avoid
        // needing a direct project reference to Twig.Infrastructure in test project)
        var typeNames = services.Select(d => d.ServiceType.Name).ToList();
        typeNames.ShouldContain("IWorkItemRepository");
        typeNames.ShouldContain("IProcessTypeStore");
        typeNames.ShouldContain("IContextStore");
        typeNames.ShouldContain("SqliteCacheStore");
        typeNames.ShouldContain("TwigPaths");
    }

    [Fact]
    public void AddPolyphonyServices_ResolvesProcessConfig()
    {
        // Arrange
        var configPath = FindProcessConfigPath();
        if (configPath is null) return;

        var services = new ServiceCollection();
        services.AddPolyphonyServices(configPath, twigDir: null);
        using var provider = services.BuildServiceProvider();

        // Act
        var config = provider.GetRequiredService<ProcessConfig>();

        // Assert
        config.ShouldNotBeNull();
        config.ProcessTemplate.ShouldNotBeNullOrWhiteSpace();
        config.Types.ShouldNotBeEmpty();
    }

    [Fact]
    public void AddPolyphonyServices_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.AddPolyphonyServices("nonexistent.yaml", twigDir: null);

        // Assert — fluent API returns the same collection
        result.ShouldBeSameAs(services);
    }

    [Fact]
    public void AddPolyphonyServices_ProcessConfigResolutionFailsForMissingFile()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddPolyphonyServices("nonexistent-config.yaml", twigDir: null);
        using var provider = services.BuildServiceProvider();

        // Act & Assert — factory throws FileNotFoundException on resolution
        Should.Throw<FileNotFoundException>(() => provider.GetRequiredService<ProcessConfig>());
    }

    [Fact]
    public void AddPolyphonyServices_AcceptsExplicitTwigDir()
    {
        // Arrange
        var services = new ServiceCollection();
        var customTwigDir = Path.Combine(Path.GetTempPath(), "test-twig-dir");

        // Act — should not throw during registration
        services.AddPolyphonyServices("any-config.yaml", twigDir: customTwigDir);

        // Assert — TwigPaths is registered (resolution deferred)
        var typeNames = services.Select(d => d.ServiceType.Name).ToList();
        typeNames.ShouldContain("TwigPaths");
    }

    private static string? FindProcessConfigPath()
    {
        var twig2Root = TestHelpers.FindRepoRoot("twig2");
        var path = Path.Combine(twig2Root, ".conductor", "process-config.yaml");
        return File.Exists(path) ? path : null;
    }
}
