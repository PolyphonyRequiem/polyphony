using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Polyphony.Models;

namespace Polyphony.Infrastructure.Research;

/// <summary>
/// Writes archivist-curated articles to a sibling research repository
/// via <see cref="IResearchStorage"/>. Handles Johnny-Decimal number
/// allocation, YAML frontmatter generation, and <c>INDEX.md</c> maintenance.
///
/// <para>
/// Designed to be <strong>idempotent on rerun</strong>: existing articles
/// at the target path are detected and skipped; duplicate INDEX.md entries
/// are not created.
/// </para>
/// </summary>
public sealed class ResearchArticleWriter(IResearchStorage storage)
{
    private static readonly Regex JdEntryPattern = new(
        @"^\s*\|\s*(\d{2}\.\d{2})\s*\|",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Writes all kept articles from the archivist output to the research
    /// repository and updates <c>INDEX.md</c>.
    /// </summary>
    /// <param name="output">The archivist's curation output.</param>
    /// <param name="workItemId">ADO work item that triggered this research.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result summarising what was written.</returns>
    public async Task<ResearchWriteArticlesResult> WriteArticlesAsync(
        ArchivistOutput output,
        int workItemId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        var kept = output.Items.Where(i =>
            i.Disposition.Equals("keep", StringComparison.OrdinalIgnoreCase)
            && i.Article is not null).ToList();
        var discarded = output.Items.Count(i =>
            i.Disposition.Equals("discard", StringComparison.OrdinalIgnoreCase));
        var expand = output.Items.Count(i =>
            i.Disposition.Equals("expand", StringComparison.OrdinalIgnoreCase));

        // Read current INDEX.md to discover existing JD numbers.
        var existingIndex = await storage.ReadAsync("INDEX.md", ct).ConfigureAwait(false) ?? "";
        var existingNumbers = ParseExistingJdNumbers(existingIndex);

        // Pre-scan category directories for existing articles so we can
        // detect slug collisions and reuse the same JD number on rerun
        // (idempotency). Maps "category/slug" → existing file path.
        var existingSlugPaths = new Dictionary<string, string>(StringComparer.Ordinal);

        var written = new List<WrittenArticle>();
        var newIndexEntries = new List<(string JdNumber, string Title, string Path)>();

        foreach (var item in kept)
        {
            var article = item.Article!;
            var slug = Slugify(article.Title);
            var categoryNormalized = NormalizeCategoryPrefix(article.Category);
            var slugKey = $"{categoryNormalized}/{slug}";

            // Check if this slug already exists in the category (from a prior run).
            string? existingPath = null;
            if (!existingSlugPaths.TryGetValue(slugKey, out existingPath))
            {
                // List the category directory once to find matching slugs.
                var categoryFiles = await storage.ListAsync(categoryNormalized, ct).ConfigureAwait(false);
                foreach (var file in categoryFiles)
                {
                    var key = ExtractSlugKey(categoryNormalized, file);
                    if (key is not null)
                        existingSlugPaths.TryAdd(key, $"{categoryNormalized}/{file}");
                }
                existingSlugPaths.TryGetValue(slugKey, out existingPath);
            }

            string jdNumber;
            string path;

            if (existingPath is not null)
            {
                // Reuse the existing JD number from the prior run.
                jdNumber = ExtractJdNumber(existingPath) ?? AllocateNextJdNumber(article.Category, existingNumbers);
                path = existingPath;
            }
            else
            {
                jdNumber = AllocateNextJdNumber(article.Category, existingNumbers);
                path = $"{categoryNormalized}/{jdNumber}-{slug}.md";

                var content = BuildArticleContent(article, jdNumber, workItemId, item.SourceRefs);
                var commitMessage = $"research: add {jdNumber} {article.Title} (AB#{workItemId})";

                await storage.WriteAsync(path, content, commitMessage, ct).ConfigureAwait(false);

                existingSlugPaths[slugKey] = path;
            }

            written.Add(new WrittenArticle
            {
                JdNumber = jdNumber,
                Title = article.Title,
                Path = path,
            });

            // Always track for index reconciliation — handles the case where
            // the article was written on a prior run but INDEX.md was not updated.
            newIndexEntries.Add((jdNumber, article.Title, path));
        }

        var indexUpdated = false;
        if (newIndexEntries.Count > 0)
        {
            // Re-read INDEX.md to get the freshest state (other articles may
            // have been written between our initial read and now).
            var freshIndex = await storage.ReadAsync("INDEX.md", ct).ConfigureAwait(false) ?? "";
            var freshNumbers = ParseExistingJdNumbers(freshIndex);

            // Filter out entries that already exist in the index.
            var entriesToAdd = newIndexEntries
                .Where(e => !freshNumbers.Contains(e.JdNumber))
                .ToList();

            if (entriesToAdd.Count > 0)
            {
                var updatedIndex = AppendToIndex(freshIndex, entriesToAdd);
                await storage.WriteAsync(
                    "INDEX.md",
                    updatedIndex,
                    $"research: update INDEX.md with {entriesToAdd.Count} article(s) (AB#{workItemId})",
                    ct).ConfigureAwait(false);
                indexUpdated = true;
            }
        }

        return new ResearchWriteArticlesResult
        {
            Articles = written,
            IndexUpdated = indexUpdated,
            TotalKept = kept.Count,
            TotalDiscarded = discarded,
            TotalExpand = expand,
        };
    }

    /// <summary>
    /// Allocates the next JD number within a category by scanning existing
    /// numbers and incrementing. Thread-safe within a single writer instance
    /// because <paramref name="existingNumbers"/> is mutated (added to) after
    /// allocation so subsequent calls within the same batch see prior allocations.
    /// </summary>
    internal static string AllocateNextJdNumber(string category, HashSet<string> existingNumbers)
    {
        // Normalize category to 2-digit zero-padded.
        if (!int.TryParse(category, CultureInfo.InvariantCulture, out var catNum))
            catNum = 10; // fallback

        var catPrefix = catNum.ToString("D2", CultureInfo.InvariantCulture);

        for (int seq = 1; seq <= 99; seq++)
        {
            var candidate = $"{catPrefix}.{seq:D2}";
            if (!existingNumbers.Contains(candidate))
            {
                existingNumbers.Add(candidate);
                return candidate;
            }
        }

        // Overflow — very unlikely in practice.
        var overflow = $"{catPrefix}.99";
        existingNumbers.Add(overflow);
        return overflow;
    }

    /// <summary>
    /// Parses existing JD numbers from an INDEX.md file. Looks for table
    /// rows matching <c>| XX.YY |</c> patterns.
    /// </summary>
    internal static HashSet<string> ParseExistingJdNumbers(string indexContent)
    {
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(indexContent)) return numbers;

        foreach (Match match in JdEntryPattern.Matches(indexContent))
        {
            numbers.Add(match.Groups[1].Value);
        }

        return numbers;
    }

    /// <summary>
    /// Builds the full article content with YAML frontmatter and body.
    /// </summary>
    internal static string BuildArticleContent(
        ArchivistArticle article,
        string jdNumber,
        int workItemId,
        IReadOnlyList<string> sourceRefs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.Append("title: \"").Append(EscapeYamlString(article.Title)).AppendLine("\"");
        sb.Append("jd_number: \"").Append(jdNumber).AppendLine("\"");
        sb.Append("work_item: ").AppendLine(workItemId.ToString(CultureInfo.InvariantCulture));
        sb.Append("captured: \"").Append(DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).AppendLine("\"");

        if (article.Topics.Count > 0)
        {
            sb.AppendLine("topics:");
            foreach (var topic in article.Topics)
            {
                sb.Append("  - \"").Append(EscapeYamlString(topic)).AppendLine("\"");
            }
        }

        if (sourceRefs.Count > 0)
        {
            sb.AppendLine("sources:");
            foreach (var source in sourceRefs)
            {
                sb.Append("  - \"").Append(EscapeYamlString(source)).AppendLine("\"");
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append("# ").AppendLine(article.Title);
        sb.AppendLine();
        sb.Append(article.BodyMarkdown.TrimEnd());
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Appends new entries to the INDEX.md content. Creates the file
    /// structure if it doesn't exist yet.
    /// </summary>
    internal static string AppendToIndex(
        string existingIndex,
        IReadOnlyList<(string JdNumber, string Title, string Path)> entries)
    {
        var sb = new StringBuilder();

        if (string.IsNullOrWhiteSpace(existingIndex))
        {
            // Create new INDEX.md with header.
            sb.AppendLine("# Research Index");
            sb.AppendLine();
            sb.AppendLine("| JD Number | Title | Path |");
            sb.AppendLine("|-----------|-------|------|");
        }
        else
        {
            sb.Append(existingIndex.TrimEnd());
            sb.AppendLine();
        }

        foreach (var (jdNumber, title, path) in entries.OrderBy(e => e.JdNumber, StringComparer.Ordinal))
        {
            sb.Append("| ").Append(jdNumber).Append(" | ").Append(title).Append(" | ").Append(path).AppendLine(" |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts a title into a URL-safe slug for file names.
    /// </summary>
    internal static string Slugify(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "untitled";

        var slug = title.ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
        slug = Regex.Replace(slug, @"[\s]+", "-");
        slug = slug.Trim('-');

        return string.IsNullOrEmpty(slug) ? "untitled" : slug;
    }

    private static string EscapeYamlString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Normalizes a category string to a 2-digit zero-padded prefix.
    /// </summary>
    internal static string NormalizeCategoryPrefix(string category)
    {
        if (!int.TryParse(category, CultureInfo.InvariantCulture, out var catNum))
            catNum = 10;
        return catNum.ToString("D2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Extracts a "category/slug" key from a file path like <c>10.01-some-title.md</c>.
    /// Returns null if the path doesn't match the expected pattern.
    /// </summary>
    internal static string? ExtractSlugKey(string category, string fileName)
    {
        // Expected: "10.01-some-title.md" → slug = "some-title"
        var name = Path.GetFileNameWithoutExtension(fileName);
        var dashIndex = name.IndexOf('-');
        if (dashIndex < 0) return null;
        var slug = name[(dashIndex + 1)..];
        return string.IsNullOrEmpty(slug) ? null : $"{category}/{slug}";
    }

    /// <summary>
    /// Extracts a JD number from a path like <c>10/10.01-slug.md</c>.
    /// Returns null if the path doesn't match the expected pattern.
    /// </summary>
    internal static string? ExtractJdNumber(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var dashIndex = fileName.IndexOf('-');
        if (dashIndex < 0) return null;
        var candidate = fileName[..dashIndex];
        // Validate it looks like XX.YY
        return Regex.IsMatch(candidate, @"^\d{2}\.\d{2}$") ? candidate : null;
    }
}
