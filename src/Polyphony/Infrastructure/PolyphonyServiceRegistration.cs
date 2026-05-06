using Microsoft.Extensions.DependencyInjection;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Routing;
using Twig.Infrastructure;

namespace Polyphony.Infrastructure;

/// <summary>
/// Registers Polyphony services into an <see cref="IServiceCollection"/>.
/// Layers Polyphony-specific registrations on top of twig core data-access services.
/// </summary>
public static class PolyphonyServiceRegistration
{
    /// <summary>
    /// Registers all services needed by Polyphony CLI commands.
    /// Delegates to <see cref="TwigServiceRegistration.AddTwigCoreServices"/> for
    /// data-access services (repositories, cache store, paths), then adds
    /// Polyphony-specific services (ProcessConfig, and in later issues: PhaseDetector,
    /// TransitionValidator, HierarchyWalker).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configPath">Path to the process-config.yaml file.</param>
    /// <param name="twigDir">Optional explicit path to the <c>.twig</c> directory.
    /// When null, <see cref="TwigServiceRegistration.AddTwigCoreServices"/> falls back
    /// to <c>CWD/.twig</c>.</param>
    public static IServiceCollection AddPolyphonyServices(
        this IServiceCollection services,
        string configPath,
        string? twigDir = null)
    {
        // Twig core services: TwigPaths, SqliteCacheStore, IWorkItemRepository,
        // IProcessTypeStore, IContextStore, and other data-access infrastructure.
        services.AddTwigCoreServices(twigDir: twigDir);

        // Polyphony-specific: ProcessConfig loaded from YAML.
        // Registered as a factory so loading is deferred until first resolution.
        services.AddSingleton(_ => ProcessConfigLoader.Load(configPath));

        // Routing services
        services.AddSingleton<PhaseDetector>();
        services.AddSingleton<HierarchyWalker>();
        services.AddSingleton<TransitionValidator>();

        // Process-shell-out infrastructure for verbs that need to talk to
        // external tools (twig CLI for side-effect operations, git, gh).
        // All stateless — singletons are safe.
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ITwigClient, TwigClient>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IGhClient, GhClient>();
        services.AddSingleton<GhTokenResolver>();

        // Run lock infrastructure (Phase 4b PR D1b).
        services.AddSingleton<Polyphony.Locking.RunLockStore>();
        services.AddSingleton<Polyphony.Locking.RunLockPathResolver>();

        return services;
    }
}
