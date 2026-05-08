using ConsoleAppFramework;
using Polyphony;
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
app.Add<AgentCommands>("agent");

// Set of registered top-level verb names + verb-group prefixes. Used by the
// Layer 2 unknown-verb pre-check below — CAF v5 treats an unknown verb the
// same as `--help` (exit 0 + usage text on stdout), which conductor can't
// distinguish from an empty success. Keep this list in sync with the
// app.Add<...>() calls above.
var knownVerbRoots = new HashSet<string>(StringComparer.Ordinal)
{
    "validate", "validate-config", "hierarchy", "health",
    "plan", "policy", "guidance", "branch", "state", "pr", "scope", "root",
    "requirements", "mg", "manifest", "lock", "worktree", "worklist", "edges", "agent",
    // Built-ins / pass-throughs handled by CAF itself.
    "--help", "-h", "--version",
};

// ── Move #2 Layer 2 — CLI dispatcher: unrecognized verb / unrecognized flag /
//                      ConsoleAppFramework parse error → routing envelope on
//                      stdout + non-zero exit. Closes #165.
//
// Pre-check: an unknown first token is rejected up front because CAF v5
// treats it the same as `--help` (exit 0 + usage text on stdout), which
// conductor can't distinguish from a silent success. We only intercept
// when there's at least one positional arg AND it doesn't match a known
// top-level verb / group prefix; bare `polyphony` and `polyphony --help`
// continue to print usage as before.
if (args.Length > 0 && !args[0].StartsWith('-') && !knownVerbRoots.Contains(args[0]))
{
    RequiredInput.EmitDispatchErrorEnvelope(
        verb: "",
        error: $"Unknown verb '{args[0]}'. Run 'polyphony --help' for the verb catalogue.");
    return ExitCodes.RoutingFailure;
}

// CAF v5 prints parse errors (unknown flag, malformed value) to STDOUT and
// sets Environment.ExitCode = 1. We capture stdout during dispatch and,
// when the exit code reports failure with no parseable JSON on stdout,
// re-emit the captured message on stdout as a routing-style envelope so
// conductor can branch on action == "error" the same way it does for
// verb-layer missing-arg envelopes.
var originalStdout = Console.Out;
var originalStderr = Console.Error;
using var stdoutCapture = new StringWriter();
using var stderrCapture = new StringWriter();
Console.SetOut(stdoutCapture);
Console.SetError(stderrCapture);
try
{
    app.Run(args);
}
catch (Exception ex)
{
    Console.SetOut(originalStdout);
    Console.SetError(originalStderr);
    RequiredInput.EmitDispatchErrorEnvelope(verb: "", error: ex.Message);
    return ExitCodes.RoutingFailure;
}
finally
{
    Console.SetOut(originalStdout);
    Console.SetError(originalStderr);
}

var capturedStdout = stdoutCapture.ToString();
var capturedStderr = stderrCapture.ToString();

if (Environment.ExitCode != ExitCodes.Success && !LooksLikeJsonEnvelope(capturedStdout))
{
    // CAF (or some other path) failed and didn't produce a JSON envelope
    // on stdout. Re-emit the captured text on stderr (so interactive users
    // still see it) and produce a routing envelope on stdout.
    if (capturedStderr.Length > 0) Console.Error.Write(capturedStderr);
    if (capturedStdout.Length > 0) Console.Error.Write(capturedStdout);
    var message = (capturedStderr + capturedStdout).Trim();
    if (message.Length == 0) message = "polyphony CLI dispatch failed without output.";
    RequiredInput.EmitDispatchErrorEnvelope(verb: "", error: message);
    return ExitCodes.RoutingFailure;
}

// Pass-through path: replay captured streams to the real streams. Stderr
// first so error-then-stdout ordering inside any single verb is preserved
// (verbs that interleave currently don't exist, but the contract should be
// transparent).
if (capturedStderr.Length > 0) Console.Error.Write(capturedStderr);
if (capturedStdout.Length > 0) Console.Out.Write(capturedStdout);

return Environment.ExitCode;

// Local helper kept beside the wiring it serves.
static bool LooksLikeJsonEnvelope(string text)
{
    var trimmed = text.AsSpan().TrimStart();
    return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
}



