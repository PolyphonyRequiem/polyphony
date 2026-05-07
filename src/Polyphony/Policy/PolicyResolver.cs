namespace Polyphony.Policy;

using Polyphony.Sdlc;

/// <summary>
/// Resolves an effective <see cref="ScopeRule"/> for a given scope + domain by layering
/// rules most-specific-wins: <c>Root</c> &gt; <c>ByType[name]</c> &gt; <c>Defaults</c>.
/// Each field is resolved independently — a more specific scope can override only
/// <c>mode</c> while inheriting all caps from defaults.
/// </summary>
public static class PolicyResolver
{
    /// <summary>
    /// Resolves the effective rule for <paramref name="scope"/> within
    /// <paramref name="domain"/>. Returns a fully-populated <see cref="ResolvedRule"/>
    /// suitable for downstream JSON consumption by workflow route conditions.
    /// </summary>
    /// <param name="config">A <see cref="PolicyConfig"/> with built-in defaults already
    /// applied (via <see cref="PolicyLoader.LoadOrDefault"/> or
    /// <see cref="PolicyLoader.ApplyBuiltInDefaults"/>).</param>
    /// <param name="domain">Approvals or PR.</param>
    /// <param name="scope">A scope token: <c>root</c>, <c>default</c>, or
    /// <c>type:Name</c> (e.g. <c>type:Issue</c>). Case-sensitive on the type name.</param>
    public static ResolvedRule Resolve(PolicyConfig config, PolicyDomain domain, string scope)
    {
        var domainPolicy = domain switch
        {
            PolicyDomain.Approvals => config.Approvals,
            PolicyDomain.Pr => config.Pr,
            PolicyDomain.OpenQuestions => config.OpenQuestions,
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };

        if (domainPolicy is null)
            throw new InvalidOperationException(
                $"Policy config has no '{domain.ToString().ToLowerInvariant()}' domain after defaults applied — bug.");

        var defaults = domainPolicy.Defaults
            ?? throw new InvalidOperationException(
                $"Policy domain '{domain}' has no defaults after defaults applied — bug.");

        var resolvedScope = scope switch
        {
            "root" => "root",
            "default" => "default",
            _ when scope.StartsWith("type:", StringComparison.Ordinal) => scope,
            _ => throw new ArgumentException(
                $"Unknown scope '{scope}'. Expected 'root', 'default', or 'type:<TypeName>'.", nameof(scope)),
        };

        ScopeRule? specific = null;
        if (resolvedScope == "root")
        {
            specific = domainPolicy.Root;
        }
        else if (resolvedScope.StartsWith("type:", StringComparison.Ordinal))
        {
            var typeName = resolvedScope["type:".Length..];
            domainPolicy.ByType?.TryGetValue(typeName, out specific);
        }

        var quality = MergeQuality(specific?.QualityThreshold, defaults.QualityThreshold);

        return new ResolvedRule
        {
            Domain = DomainToString(domain),
            Scope = resolvedScope,
            Mode = (specific?.Mode ?? defaults.Mode ?? PolicyMode.Warning).ToString().ToLowerInvariant(),
            MaxRevisionCycles = specific?.MaxRevisionCycles ?? defaults.MaxRevisionCycles,
            MaxFixLoops = specific?.MaxFixLoops ?? defaults.MaxFixLoops,
            MaxRemediationCycles = specific?.MaxRemediationCycles ?? defaults.MaxRemediationCycles,
            MinSeverity = (specific?.MinSeverity ?? defaults.MinSeverity)?.ToString().ToLowerInvariant(),
            MaxQuestionLoops = specific?.MaxQuestionLoops ?? defaults.MaxQuestionLoops,
            QualityAvgScoreAtLeast = quality?.AvgScoreAtLeast,
            QualityBlockingCountAtMost = quality?.BlockingCountAtMost,
        };
    }

    private static QualityThreshold? MergeQuality(QualityThreshold? specific, QualityThreshold? defaults)
    {
        if (specific is null) return defaults;
        if (defaults is null) return specific;
        return new QualityThreshold
        {
            AvgScoreAtLeast = specific.AvgScoreAtLeast ?? defaults.AvgScoreAtLeast,
            BlockingCountAtMost = specific.BlockingCountAtMost ?? defaults.BlockingCountAtMost,
        };
    }

    /// <summary>
    /// Resolves the effective per-item guidance configuration for
    /// <paramref name="scope"/>, layering <c>guidance.by_type[name]</c> over
    /// the workspace defaults (top-level <c>guidance.source</c> +
    /// <c>guidance.ado_field_name</c>).
    /// </summary>
    /// <param name="config">A <see cref="PolicyConfig"/> with built-in defaults
    /// already applied.</param>
    /// <param name="scope">A scope token: <c>default</c> or <c>type:Name</c>.
    /// <c>root</c> is accepted and treated as <c>default</c> — guidance has no
    /// notion of run-root scope.</param>
    /// <returns>A fully-populated <see cref="GuidanceConfig"/>.</returns>
    public static GuidanceConfig ResolveGuidance(PolicyConfig config, string scope)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(scope);

        var guidance = config.Guidance
            ?? throw new InvalidOperationException(
                "Policy config has no 'guidance' block after defaults applied — bug.");

        var defaultSource = guidance.Source ?? GuidanceSource.DescriptionBlock;
        var defaultField = guidance.AdoFieldName;

        // Most-specific wins: type:Name overlays the workspace default; root + default
        // both fall through to the top-level defaults (guidance has no Root concept).
        if (scope.StartsWith("type:", StringComparison.Ordinal))
        {
            var typeName = scope["type:".Length..];
            if (guidance.ByType is not null && guidance.ByType.TryGetValue(typeName, out var rule))
            {
                return new GuidanceConfig(
                    Source: rule.Source ?? defaultSource,
                    AdoFieldName: rule.AdoFieldName ?? defaultField);
            }

            return new GuidanceConfig(defaultSource, defaultField);
        }

        if (scope is "default" or "root")
            return new GuidanceConfig(defaultSource, defaultField);

        throw new ArgumentException(
            $"Unknown scope '{scope}'. Expected 'default', 'root', or 'type:<TypeName>'.", nameof(scope));
    }

    private static string DomainToString(PolicyDomain domain) => domain switch
    {
        PolicyDomain.OpenQuestions => "open_questions",
        _ => domain.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// Output shape from <see cref="PolicyResolver.Resolve"/> — fully resolved rule
/// suitable for downstream JSON consumption by workflow route conditions.
/// </summary>
public sealed record ResolvedRule
{
    public required string Domain { get; init; }
    public required string Scope { get; init; }
    public required string Mode { get; init; }
    public int? MaxRevisionCycles { get; init; }
    public int? MaxFixLoops { get; init; }
    public int? MaxRemediationCycles { get; init; }
    public string? MinSeverity { get; init; }
    public int? MaxQuestionLoops { get; init; }
    public int? QualityAvgScoreAtLeast { get; init; }
    public int? QualityBlockingCountAtMost { get; init; }
}
