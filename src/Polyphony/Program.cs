using ConsoleAppFramework;
using Polyphony.Commands;
using Polyphony.Infrastructure;
using Twig.Infrastructure.Config;

SQLitePCL.Batteries.Init();

// Prevent gh CLI from hanging on credential prompts in non-TTY environments
// (e.g. when spawned by conductor). See invoke-gh.ps1 in the twig registry
// for the original pattern and AB#3000 for the hang this prevents.
Environment.SetEnvironmentVariable("GH_PROMPT_DISABLED", "1");

var app = ConsoleApp.Create()
    .ConfigureServices(services =>
    {
        var twigDir = WorkspaceDiscovery.FindTwigDir()
            ?? Path.Combine(Directory.GetCurrentDirectory(), ".twig");
        var configPath = Path.Combine(Directory.GetCurrentDirectory(), ".conductor", "process-config.yaml");

        services.AddPolyphonyServices(configPath, twigDir);
    });

app.Add<RouteCommand>();
app.Add<ValidateCommand>();
app.Add<ValidateConfigCommand>();
app.Add<HierarchyCommand>();
app.Add<HealthCommand>();
app.Add<PlanCommands>("plan");
app.Add<PolicyCommands>("policy");
app.Add<GuidanceCommands>("guidance");
app.Add<BranchCommands>("branch");
app.Add<StateCommands>("state");
app.Add<PrCommands>("pr");
app.Add<ScopeCommands>("scope");
app.Add<RootCommands>("root");
app.Add<RequirementsCommands>("requirements");
app.Add<MgCommands>("mg");
app.Add<ManifestCommands>("manifest");
app.Add<LockCommands>("lock");
app.Add<WorktreeCommands>("worktree");
app.Add<WorklistCommands>("worklist");
app.Add<EdgesCommands>("edges");
app.Run(args);
