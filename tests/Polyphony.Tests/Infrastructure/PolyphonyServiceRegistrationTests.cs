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

    /// <summary>
    /// Asserts every CLI command class registered in <c>Program.cs</c> has all
    /// of its constructor dependencies registered in
    /// <see cref="PolyphonyServiceRegistration"/>. This catches the
    /// regression class where one command class declares a constructor
    /// dependency on another (e.g. <c>RootCommands</c> depends on
    /// <c>ScopeCommands</c>) but the dependency is not registered. Without
    /// this test the failure mode is silent: ConsoleAppFramework constructs
    /// the command with a null parameter and the verb NREs at first use
    /// (see commit history for the <c>polyphony root declare</c> NPE).
    ///
    /// Reflection-based on purpose — does not trigger DI resolution, so it
    /// runs without a live twig workspace, ADO connectivity, or a process
    /// config file.
    /// </summary>
    [Theory]
    [InlineData(typeof(Polyphony.Commands.ValidateCommand))]
    [InlineData(typeof(Polyphony.Commands.ValidateConfigCommand))]
    [InlineData(typeof(Polyphony.Commands.HierarchyCommand))]
    [InlineData(typeof(Polyphony.Commands.HealthCommand))]
    [InlineData(typeof(Polyphony.Commands.PlanCommands))]
    [InlineData(typeof(Polyphony.Commands.PolicyCommands))]
    [InlineData(typeof(Polyphony.Commands.GuidanceCommands))]
    [InlineData(typeof(Polyphony.Commands.BranchCommands))]
    [InlineData(typeof(Polyphony.Commands.StateCommands))]
    [InlineData(typeof(Polyphony.Commands.PrCommands))]
    [InlineData(typeof(Polyphony.Commands.ScopeCommands))]
    [InlineData(typeof(Polyphony.Commands.RootCommands))]
    [InlineData(typeof(Polyphony.Commands.RequirementsCommands))]
    [InlineData(typeof(Polyphony.Commands.MergeGroupCommands))]
    [InlineData(typeof(Polyphony.Commands.ManifestCommands))]
    [InlineData(typeof(Polyphony.Commands.LockCommands))]
    [InlineData(typeof(Polyphony.Commands.WorktreeCommands))]
    [InlineData(typeof(Polyphony.Commands.WorklistCommands))]
    [InlineData(typeof(Polyphony.Commands.EdgesCommands))]
    [InlineData(typeof(Polyphony.Commands.AgentCommands))]
    public void Command_ConstructorDependenciesAreRegistered(Type commandType)
    {
        // Arrange — production registration ONLY; do not register the command
        // itself, because we are asserting that AddPolyphonyServices alone
        // covers every dep a command's constructor needs.
        var services = new ServiceCollection();
        services.AddPolyphonyServices("nonexistent-config.yaml", twigDir: null);

        var registeredTypes = services.Select(d => d.ServiceType).ToHashSet();
        var ctor = commandType.GetConstructors()
            .Where(c => c.IsPublic)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        // Act + Assert — every constructor parameter type must resolve from
        // the production container. Params with explicit defaults
        // (e.g. `IFoo? foo = null`) are skipped because the constructor
        // tolerates a missing registration; they are not the bug class
        // we are guarding against.
        foreach (var param in ctor.GetParameters())
        {
            if (param.HasDefaultValue) continue;

            registeredTypes.ShouldContain(
                param.ParameterType,
                $"{commandType.Name} constructor param '{param.Name}' of type " +
                $"{param.ParameterType.Name} is not registered by " +
                $"PolyphonyServiceRegistration. ConsoleAppFramework will " +
                $"resolve it as null and the verb will NRE at first use.");
        }
    }

    private static string? FindProcessConfigPath()
    {
        var twig2Root = TestHelpers.FindRepoRoot("twig2");
        var path = Path.Combine(twig2Root, ".conductor", "process-config.yaml");
        return File.Exists(path) ? path : null;
    }
}
