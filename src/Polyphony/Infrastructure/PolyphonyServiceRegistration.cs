using Microsoft.Extensions.DependencyInjection;
using Polyphony.Configuration;
using Polyphony.Infrastructure.AzureDevOps;
using Polyphony.Infrastructure.Processes;
using Polyphony.Infrastructure.Research;
using Polyphony.Postconditions;
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

        // Sdlc observers — singleton services that wrap IGitClient/IGhClient/ITwigClient
        // to produce per-RequirementKind observations. Shared by routing-style verbs
        // (e.g. plan detect-state) and the next-ready worklist builder.
        services.AddSingleton<Sdlc.Observers.PlanObserver>();

        // Process-shell-out infrastructure for verbs that need to talk to
        // external tools (twig CLI for side-effect operations, git, gh).
        // All stateless — singletons are safe.
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<ITwigClient, TwigClient>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IGhClient, GhClient>();
        services.AddSingleton<GhTokenResolver>();

        // Shared "is the post-condition met on origin?" check used by every
        // commit-and-push verb. Stateless wrapper over IGitClient — singleton-safe.
        // See Polyphony.Postconditions.IPostconditionVerifier for the contract.
        services.AddSingleton<IPostconditionVerifier, PostconditionVerifier>();

        // Azure DevOps REST infrastructure (Phase 5 scaffolding — auth probe only).
        // HttpClient is registered as a singleton to avoid socket exhaustion under
        // repeated CLI invocations and to share the underlying handler pool. The
        // AdoTokenResolver reads PAT env vars and is also stateless / singleton-safe.
        services.AddSingleton<HttpClient>(_ => new HttpClient());
        services.AddSingleton<AdoTokenResolver>();
        services.AddSingleton<IAdoClient, AdoClient>();

        // Run lock infrastructure (Phase 4b PR D1b).
        services.AddSingleton<Polyphony.Locking.RunLockStore>();
        services.AddSingleton<Polyphony.Locking.RunLockPathResolver>();

        // Per-root state path resolver (Rev 4.2 amendment): manifest +
        // run-lock paths under <git-common-dir>/polyphony/<root_id>/.
        services.AddSingleton<Polyphony.Infrastructure.Paths.PolyphonyStatePaths>();

        // Command classes that are referenced as constructor dependencies of
        // OTHER command classes must be registered explicitly. ConsoleAppFramework
        // resolves top-level command parameters from the container but does not
        // auto-register sibling command classes. RootCommands depends on
        // ScopeCommands; without this registration, ScopeCommands resolves to
        // null and `polyphony root declare` NREs at first call. Verified by
        // PolyphonyServiceRegistrationTests.Command_ResolvesCleanlyFromDI.
        services.AddSingleton<Commands.ScopeCommands>();

        // Profile config (profile.yaml) — deferred load like ProcessConfig.
        // Sits alongside process-config.yaml in .polyphony-config/.
        var profilePath = Path.Combine(Path.GetDirectoryName(configPath) ?? ".", "profile.yaml");
        services.AddSingleton(_ => ProfileConfigLoader.Load(profilePath));

        // Research storage — backed by the GitHub Contents API when configured,
        // otherwise a null implementation that degrades gracefully on reads
        // and throws on writes (preventing silent data loss).
        services.AddSingleton<IResearchStorage>(sp =>
        {
            var profile = sp.GetRequiredService<ProfileConfig>();
            if (profile.Research is { Repository: { Length: > 0 } })
            {
                var runner = sp.GetRequiredService<IProcessRunner>();
                return new GitHubResearchStorage(profile.Research, runner);
            }
            return new NullResearchStorage();
        });

        // Research article writer — writes archivist-curated articles to
        // the sibling research repo with JD layout and INDEX.md maintenance.
        services.AddSingleton<ResearchArticleWriter>();

        return services;
    }
}
