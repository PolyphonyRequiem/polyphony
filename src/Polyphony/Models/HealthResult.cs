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
    public bool AllCriticalPassed => Checks.All(c => c.Success || !IsCritical(c.Name));

    private static bool IsCritical(string name) =>
        name is "process-config" or "twig" or "git";
}
