using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Polyphony.Sdlc;

/// <summary>
/// Outcome of a strict-parse over the YAML front-matter block at the top of
/// a <c>plans/plan-{id}.md</c> file. Distinguishes Absent (no fence) from
/// Malformed (bad YAML / wrong shape) from Present (well-formed), so the
/// caller can route on the failure case rather than swallowing it as
/// "no override declared".
/// </summary>
public sealed record PlanFileFrontMatterResult
{
    public required PlanFileFrontMatterStatus Status { get; init; }

    /// <summary>
    /// The architect-declared <c>apex_facets</c> list, normalised through
    /// <see cref="FacetTagParser.ParseFacets"/>: alphabetical, lowercase,
    /// deduplicated. Empty when the field is absent or its list is empty.
    /// </summary>
    public required IReadOnlyList<string> ApexFacets { get; init; }

    /// <summary>
    /// The unknown facet tokens that caused <see cref="Status"/> to be
    /// <see cref="PlanFileFrontMatterStatus.Malformed"/>; verbatim, original
    /// casing. Empty when the failure is for a different reason or the parse
    /// succeeded.
    /// </summary>
    public required IReadOnlyList<string> UnknownFacets { get; init; }

    /// <summary>Reason for malformed status; null otherwise.</summary>
    public string? ErrorDetail { get; init; }
}

/// <summary>Parse outcome categories — see <see cref="PlanFileFrontMatterResult"/>.</summary>
public enum PlanFileFrontMatterStatus
{
    /// <summary>No <c>---</c>-fenced block at the top of the file.</summary>
    Absent,

    /// <summary>Fence found but the YAML body failed to parse, or a recognised
    /// key carried the wrong shape.</summary>
    Malformed,

    /// <summary>Well-formed YAML mapping. Recognised keys are absent or
    /// well-shaped; unrecognised keys are ignored (forward-compat).</summary>
    Present,
}

/// <summary>
/// Strict YAML front-matter parser for <c>plans/plan-{id}.md</c> files.
/// Mirrors <see cref="Polyphony.Commands.PlanPrFrontMatter"/>'s pattern
/// (regex fence + YamlDotNet load) but is owned by the SDLC layer because
/// the recognised keys (<c>apex_facets</c>) drive deriver inputs, not PR
/// rendering.
///
/// <para>Recognised keys (closed-loop PR #7):</para>
/// <list type="bullet">
///   <item><c>apex_facets: [implementable]</c> — per-item facet override.
///         Each entry must be a canonical <see cref="Facet"/> name; unknown
///         entries cause <see cref="PlanFileFrontMatterStatus.Malformed"/>.</item>
/// </list>
///
/// <para>This is opt-in: existing plans without front-matter return Absent
/// and the caller falls back to default behaviour. The fence MUST be at
/// the very start of the file (no leading whitespace allowed) so the
/// parser cannot misread prose later in the body.</para>
/// </summary>
public static class PlanFileFrontMatter
{
    private static readonly Regex FenceRegex = new(
        @"\A---\s*\r?\n(?<yaml>.*?)\r?\n---\s*(\r?\n|$)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Parse front-matter from the literal text of a plan markdown file.
    /// Returns Absent for empty / no-fence input, Malformed for bad YAML or
    /// wrong-shape values, Present otherwise.
    /// </summary>
    public static PlanFileFrontMatterResult Parse(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return Absent();
        }

        var match = FenceRegex.Match(body);
        if (!match.Success)
        {
            return Absent();
        }

        var yamlText = match.Groups["yaml"].Value;
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return Absent();
        }

        YamlStream yaml;
        try
        {
            yaml = new YamlStream();
            using var reader = new StringReader(yamlText);
            yaml.Load(reader);
        }
        catch (YamlException ex)
        {
            return Malformed($"YAML parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Malformed($"YAML parse error: {ex.Message}");
        }

        if (yaml.Documents.Count == 0)
        {
            return Absent();
        }

        if (yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            return Malformed("Front-matter root must be a YAML mapping.");
        }

        IReadOnlyList<string> apexFacets = [];
        if (root.Children.TryGetValue(new YamlScalarNode("apex_facets"), out var facetsNode))
        {
            if (facetsNode is not YamlSequenceNode seq)
            {
                return Malformed("'apex_facets' must be a YAML sequence of facet name strings.");
            }

            var tokens = new List<string>(seq.Children.Count);
            foreach (var entry in seq.Children)
            {
                if (entry is not YamlScalarNode scalar || string.IsNullOrEmpty(scalar.Value))
                {
                    return Malformed("'apex_facets' entries must be non-empty scalar strings.");
                }
                tokens.Add(scalar.Value!);
            }

            var parse = FacetTagParser.ParseFacets(tokens);
            if (!parse.IsValid)
            {
                return new PlanFileFrontMatterResult
                {
                    Status = PlanFileFrontMatterStatus.Malformed,
                    ApexFacets = [],
                    UnknownFacets = parse.UnknownFacets,
                    ErrorDetail = $"'apex_facets' contains unknown facet name(s): {string.Join(", ", parse.UnknownFacets)}. Allowed: {Facet.Plannable}, {Facet.Actionable}, {Facet.Implementable}.",
                };
            }

            apexFacets = parse.Facets;
        }

        return new PlanFileFrontMatterResult
        {
            Status = PlanFileFrontMatterStatus.Present,
            ApexFacets = apexFacets,
            UnknownFacets = [],
            ErrorDetail = null,
        };
    }

    private static PlanFileFrontMatterResult Absent() => new()
    {
        Status = PlanFileFrontMatterStatus.Absent,
        ApexFacets = [],
        UnknownFacets = [],
        ErrorDetail = null,
    };

    private static PlanFileFrontMatterResult Malformed(string detail) => new()
    {
        Status = PlanFileFrontMatterStatus.Malformed,
        ApexFacets = [],
        UnknownFacets = [],
        ErrorDetail = detail,
    };
}
