using ConsoleAppFramework;
using Polyphony.Commands;

var app = ConsoleApp.Create();
app.Add<RouteCommand>();
app.Add<ValidateCommand>();
app.Add<HierarchyCommand>();
app.Run(args);
