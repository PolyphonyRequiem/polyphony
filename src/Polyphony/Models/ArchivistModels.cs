using System.Text.Json.Serialization;

namespace Polyphony.Models;

/// <summary>
/// A single curation item from the archivist agent. Each item represents
/// one research finding that the archivist has evaluated for inclusion
/// in the sibling research repository.
/// </summary>
public sealed record ArchivistCurationItem
{
    /// <summary>
    /// The archivist's verdict: <c>keep</c>, <c>discard</c>, or <c>expand</c>.
    /// </summary>
    public required string Disposition { get; init; }

    /// <summary>Brief explanation of why the archivist chose this disposition.</summary>
    public required string Rationale { get; init; }

    /// <summary>
    /// Source references from the original research findings that this
    /// curation item covers. A kept article may synthesize multiple sources.
    /// </summary>
    public required IReadOnlyList<string> SourceRefs { get; init; }

    /// <summary>
    /// Present only when <see cref="Disposition"/> is <c>keep</c>.
    /// Contains the article content to be written to the research repo.
    /// </summary>
    public ArchivistArticle? Article { get; init; }
}

/// <summary>
/// Article content for a kept curation item, authored by the archivist agent.
/// Frontmatter and JD numbering are added by <see cref="Infrastructure.Research.ResearchArticleWriter"/>
/// at write time — the archivist provides the raw content and metadata hints.
/// </summary>
public sealed record ArchivistArticle
{
    /// <summary>Human-readable title for the article.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Markdown body of the article (without frontmatter — that is
    /// generated deterministically by the writer).
    /// </summary>
    public required string BodyMarkdown { get; init; }

    /// <summary>
    /// Johnny-Decimal category hint (e.g. <c>"10"</c>, <c>"20"</c>).
    /// The writer uses this to place the article under the right
    /// category directory and allocate the next sequential number.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>Topic tags for the article's frontmatter.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];
}

/// <summary>
/// Top-level payload emitted by the archivist agent. Contains the
/// per-finding curation decisions and a summary of the overall pass.
/// </summary>
public sealed record ArchivistOutput
{
    /// <summary>The curation decisions, one per research finding.</summary>
    public required IReadOnlyList<ArchivistCurationItem> Items { get; init; }

    /// <summary>One-line summary of the curation pass for logging.</summary>
    public string Summary { get; init; } = "";
}

/// <summary>
/// Result of writing curated articles to the sibling research repository.
/// Emitted by <c>polyphony research write-articles</c>.
/// </summary>
public sealed record ResearchWriteArticlesResult
{
    /// <summary>Articles that were successfully written.</summary>
    public required IReadOnlyList<WrittenArticle> Articles { get; init; }

    /// <summary>Whether INDEX.md was updated as part of this write.</summary>
    public required bool IndexUpdated { get; init; }

    /// <summary>Count of items with disposition <c>keep</c>.</summary>
    public required int TotalKept { get; init; }

    /// <summary>Count of items with disposition <c>discard</c>.</summary>
    public required int TotalDiscarded { get; init; }

    /// <summary>Count of items with disposition <c>expand</c>.</summary>
    public required int TotalExpand { get; init; }
}

/// <summary>
/// A single article written to the sibling research repository.
/// </summary>
public sealed record WrittenArticle
{
    /// <summary>Assigned Johnny-Decimal number (e.g. <c>"10.01"</c>).</summary>
    public required string JdNumber { get; init; }

    /// <summary>Article title from the archivist.</summary>
    public required string Title { get; init; }

    /// <summary>Repo-relative path where the article was written.</summary>
    public required string Path { get; init; }
}
