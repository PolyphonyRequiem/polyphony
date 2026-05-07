using Polyphony.Sdlc;
using YamlDotNet.Serialization;

namespace Polyphony.Configuration;

/// <summary>
/// YAML-friendly DTO for a single entry under the top-level <c>facets:</c>
/// block of <c>process-config.yaml</c>. Mutable string-array properties
/// because <c>YamlDotNet</c> populates POCOs through public setters and the
/// canonical <see cref="Sdlc.FacetProfile"/> record uses immutable
/// constructor parameters.
/// </summary>
/// <remarks>
/// Use <see cref="ProcessConfigFacetExtensions.GetFacetProfiles"/> to obtain
/// the strongly-typed dictionary the
/// <see cref="Sdlc.FacetProfileComposer"/> consumes.
/// </remarks>
public sealed class FacetProfileConfig
{
    [YamlMember(Alias = "skills")]
    public string[] Skills { get; set; } = [];

    [YamlMember(Alias = "mcps")]
    public string[] Mcps { get; set; } = [];
}

/// <summary>
/// Conversions between the YAML-loaded <see cref="FacetProfileConfig"/>
/// shape and the canonical <see cref="FacetProfile"/> record consumed by
/// the composer.
/// </summary>
public static class ProcessConfigFacetExtensions
{
    /// <summary>
    /// Materialize the process config's top-level <c>facets:</c> block as a
    /// dictionary of canonical <see cref="FacetProfile"/> records suitable
    /// for <see cref="FacetProfileComposer.Compose"/>. Returns an empty
    /// dictionary when no facets block was supplied.
    /// </summary>
    public static IReadOnlyDictionary<string, FacetProfile> GetFacetProfiles(this ProcessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Facets is null || config.Facets.Count == 0)
        {
            return new Dictionary<string, FacetProfile>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, FacetProfile>(config.Facets.Count, StringComparer.Ordinal);
        foreach (var (name, profile) in config.Facets)
        {
            result[name] = new FacetProfile(
                Skills: profile.Skills ?? [],
                Mcps: profile.Mcps ?? []);
        }
        return result;
    }
}
