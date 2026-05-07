namespace Polyphony;

public sealed record HealthCheckResult
{
    public required string Name { get; init; }
    public required bool Success { get; init; }
    public string? Message { get; init; }
    public string? Details { get; init; }
}

public sealed record HealthResult
{
    public required HealthCheckResult[] Checks { get; init; }
    public string? Os { get; init; }
    public string? Architecture { get; init; }
    public string? DotnetVersion { get; init; }
    public string? PolyphonyVersion { get; init; }

    // The canonical SDLC entry-point workflow reference (workflow_name@process_template).
    // Surfaced so a first-time user running `polyphony health` gets a breadcrumb
    // pointing at the actual conductor entry, instead of having to grep the registry.
    // Hardcoded — pulling the version from .conductor/registry/index.yaml at runtime
    // would require parsing the registry from inside HealthCommand for no real win.
    public string? CanonicalWorkflow { get; init; }

    public bool AllCriticalPassed => Checks.All(c => c.Success || !IsCritical(c.Name));

    private static bool IsCritical(string name) =>
        name is "process-config" or "twig" or "git" or "dotnet-version" or "aot-support" or "sqlite" or "sqlite-wal" or "yamldotnet";
}
