using System.Text.RegularExpressions;
using Polyphony.Sdlc;
using Twig.Domain.Aggregates;

namespace Polyphony.Guidance;

/// <summary>
/// Pure extraction of per-item guidance from a <see cref="WorkItem"/>. The
/// description-block source works on any platform (the convention is just
/// HTML comments in the description); the ADO-field source is opt-in and
/// only consulted when <see cref="GuidanceConfig.Source"/> is
/// <see cref="GuidanceSource.AdoField"/>.
/// </summary>
/// <remarks>
/// <para>
/// This module is the EXTRACTION mechanism. Injection of the extracted text
/// into the agent prompt happens in the driver (Phase 6 PR #5).
/// </para>
/// <para>
/// Returns null when no guidance is present — distinguishable from the empty
/// string so callers can branch on "has guidance" without false positives on
/// blocks that contain only whitespace.
/// </para>
/// </remarks>
public static class GuidanceExtractor
{
    /// <summary>
    /// Field reference name for the ADO description, used by the
    /// <see cref="GuidanceSource.DescriptionBlock"/> source.
    /// </summary>
    internal const string DescriptionFieldName = "System.Description";

    /// <summary>
    /// Separator inserted between concatenated description blocks when a work
    /// item carries more than one. Designed to be visually obvious in the
    /// agent prompt and to introduce a paragraph break.
    /// </summary>
    internal const string BlockSeparator = "\n\n---\n\n";

    /// <summary>
    /// Regex matching one fenced guidance block. Case-sensitive on the tag
    /// name as documented; Singleline so <c>.</c> spans newlines.
    /// </summary>
    /// <remarks>
    /// An opening tag without a closing tag does not match — those are
    /// silently skipped. The validator surface for work-item content lint
    /// does not yet exist; surfacing such misformed blocks is deferred to a
    /// future polish PR (see Phase 6 design sketch open question #5).
    /// </remarks>
    private static readonly Regex GuidanceBlockRegex = new(
        @"<!-- polyphony:guidance -->(.*?)<!-- /polyphony:guidance -->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Extracts guidance from <paramref name="workItem"/> per the configured
    /// <paramref name="config"/>. Returns null when no guidance is present
    /// (NOT empty string). When multiple description blocks exist, their
    /// trimmed contents are concatenated with <c>\n\n---\n\n</c>.
    /// </summary>
    /// <param name="workItem">The work item to inspect. Must not be null.</param>
    /// <param name="config">Resolved guidance configuration; <see cref="GuidanceConfig.Source"/>
    /// must be one of the <see cref="GuidanceSource"/> constants.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="workItem"/> or <paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException">When <see cref="GuidanceConfig.Source"/> is unknown,
    /// or when source is <see cref="GuidanceSource.AdoField"/> but
    /// <see cref="GuidanceConfig.AdoFieldName"/> is null/empty.</exception>
    public static string? Extract(WorkItem workItem, GuidanceConfig config)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(config);

        return config.Source switch
        {
            GuidanceSource.DescriptionBlock => ExtractFromDescription(workItem),
            GuidanceSource.AdoField => ExtractFromAdoField(workItem, config.AdoFieldName),
            _ => throw new ArgumentException(
                $"Unknown guidance source '{config.Source}'. Expected '{GuidanceSource.DescriptionBlock}' or '{GuidanceSource.AdoField}'.",
                nameof(config)),
        };
    }

    private static string? ExtractFromDescription(WorkItem workItem)
    {
        if (!workItem.Fields.TryGetValue(DescriptionFieldName, out var description))
            return null;
        if (string.IsNullOrEmpty(description))
            return null;

        var matches = GuidanceBlockRegex.Matches(description);
        if (matches.Count == 0)
            return null;

        var parts = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            // Group 1 is the inner content; trim outer whitespace, preserve inner.
            var inner = match.Groups[1].Value.Trim();
            parts.Add(inner);
        }

        return string.Join(BlockSeparator, parts);
    }

    private static string? ExtractFromAdoField(WorkItem workItem, string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException(
                $"GuidanceConfig.AdoFieldName must be set when source is '{GuidanceSource.AdoField}'.",
                nameof(fieldName));

        if (!workItem.Fields.TryGetValue(fieldName, out var value))
            return null;
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value;
    }
}
