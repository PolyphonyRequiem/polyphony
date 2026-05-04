using ConsoleAppFramework;
using Polyphony.Commands;
using Polyphony.Infrastructure;
using Twig.Infrastructure.Config;

SQLitePCL.Batteries.Init();

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
app.Add<BranchCommands>("branch");
app.Add<StateCommands>("state");
app.Run(args);
