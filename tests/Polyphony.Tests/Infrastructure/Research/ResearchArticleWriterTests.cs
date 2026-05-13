using NSubstitute;
using Polyphony.Infrastructure.Research;
using Polyphony.Models;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Research;

public sealed class ResearchArticleWriterTests
{
    private readonly IResearchStorage _storage = Substitute.For<IResearchStorage>();

    #region AllocateNextJdNumber

    [Fact]
    public void AllocateNextJdNumber_EmptySet_Returns01()
    {
        var existing = new HashSet<string>();
        var result = ResearchArticleWriter.AllocateNextJdNumber("10", existing);
        result.ShouldBe("10.01");
    }

    [Fact]
    public void AllocateNextJdNumber_ExistingNumbers_ReturnsNextSequential()
    {
        var existing = new HashSet<string> { "10.01", "10.02" };
        var result = ResearchArticleWriter.AllocateNextJdNumber("10", existing);
        result.ShouldBe("10.03");
    }

    [Fact]
    public void AllocateNextJdNumber_Gap_FillsGap()
    {
        var existing = new HashSet<string> { "10.01", "10.03" };
        var result = ResearchArticleWriter.AllocateNextJdNumber("10", existing);
        result.ShouldBe("10.02");
    }

    [Fact]
    public void AllocateNextJdNumber_SingleDigitCategory_PadsToTwoDigits()
    {
        var existing = new HashSet<string>();
        var result = ResearchArticleWriter.AllocateNextJdNumber("5", existing);
        result.ShouldBe("05.01");
    }

    [Fact]
    public void AllocateNextJdNumber_NonNumericCategory_FallsBackTo10()
    {
        var existing = new HashSet<string>();
        var result = ResearchArticleWriter.AllocateNextJdNumber("abc", existing);
        result.ShouldBe("10.01");
    }

    [Fact]
    public void AllocateNextJdNumber_MutatesExistingSet()
    {
        var existing = new HashSet<string>();
        ResearchArticleWriter.AllocateNextJdNumber("10", existing);
        existing.ShouldContain("10.01");

        // Second allocation in same batch gets next number.
        var second = ResearchArticleWriter.AllocateNextJdNumber("10", existing);
        second.ShouldBe("10.02");
    }

    #endregion

    #region ParseExistingJdNumbers

    [Fact]
    public void ParseExistingJdNumbers_EmptyString_ReturnsEmpty()
    {
        ResearchArticleWriter.ParseExistingJdNumbers("").ShouldBeEmpty();
    }

    [Fact]
    public void ParseExistingJdNumbers_ValidIndex_ExtractsNumbers()
    {
        var index = """
            # Research Index

            | JD Number | Title | Path |
            |-----------|-------|------|
            | 10.01 | First Article | 10/10.01-first.md |
            | 20.03 | Another Article | 20/20.03-another.md |
            """;

        var result = ResearchArticleWriter.ParseExistingJdNumbers(index);
        result.ShouldContain("10.01");
        result.ShouldContain("20.03");
        result.Count.ShouldBe(2);
    }

    [Fact]
    public void ParseExistingJdNumbers_NoTableRows_ReturnsEmpty()
    {
        var index = "# Research Index\n\nJust some text.";
        ResearchArticleWriter.ParseExistingJdNumbers(index).ShouldBeEmpty();
    }

    #endregion

    #region Slugify

    [Fact]
    public void Slugify_NormalTitle_ProducesSlug()
    {
        ResearchArticleWriter.Slugify("CLI Command Pattern").ShouldBe("cli-command-pattern");
    }

    [Fact]
    public void Slugify_SpecialCharacters_Removed()
    {
        ResearchArticleWriter.Slugify("JSON (Source-Gen) Patterns!").ShouldBe("json-source-gen-patterns");
    }

    [Fact]
    public void Slugify_EmptyString_ReturnsUntitled()
    {
        ResearchArticleWriter.Slugify("").ShouldBe("untitled");
    }

    [Fact]
    public void Slugify_WhitespaceOnly_ReturnsUntitled()
    {
        ResearchArticleWriter.Slugify("   ").ShouldBe("untitled");
    }

    #endregion

    #region BuildArticleContent

    [Fact]
    public void BuildArticleContent_IncludesFrontmatterAndBody()
    {
        var article = new ArchivistArticle
        {
            Title = "Test Article",
            BodyMarkdown = "Some content here.",
            Category = "10",
            Topics = ["cli", "patterns"],
        };

        var result = ResearchArticleWriter.BuildArticleContent(
            article, "10.01", 3135, ["src/Example.cs"]);

        result.ShouldStartWith("---");
        result.ShouldContain("title: \"Test Article\"");
        result.ShouldContain("jd_number: \"10.01\"");
        result.ShouldContain("work_item: 3135");
        result.ShouldContain("captured: \"");
        result.ShouldContain("topics:");
        result.ShouldContain("  - \"cli\"");
        result.ShouldContain("  - \"patterns\"");
        result.ShouldContain("sources:");
        result.ShouldContain("  - \"src/Example.cs\"");
        result.ShouldContain("# Test Article");
        result.ShouldContain("Some content here.");
    }

    [Fact]
    public void BuildArticleContent_NoTopicsOrSources_OmitsSections()
    {
        var article = new ArchivistArticle
        {
            Title = "Minimal",
            BodyMarkdown = "Body.",
            Category = "10",
            Topics = [],
        };

        var result = ResearchArticleWriter.BuildArticleContent(
            article, "10.01", 100, []);

        result.ShouldNotContain("topics:");
        result.ShouldNotContain("sources:");
    }

    [Fact]
    public void BuildArticleContent_EscapesQuotesInTitle()
    {
        var article = new ArchivistArticle
        {
            Title = "Title with \"quotes\"",
            BodyMarkdown = "Body.",
            Category = "10",
        };

        var result = ResearchArticleWriter.BuildArticleContent(
            article, "10.01", 100, []);

        result.ShouldContain("title: \"Title with \\\"quotes\\\"\"");
    }

    #endregion

    #region AppendToIndex

    [Fact]
    public void AppendToIndex_EmptyIndex_CreatesHeader()
    {
        var result = ResearchArticleWriter.AppendToIndex("",
            [("10.01", "First", "10/10.01-first.md")]);

        result.ShouldContain("# Research Index");
        result.ShouldContain("| JD Number | Title | Path |");
        result.ShouldContain("| 10.01 | First | 10/10.01-first.md |");
    }

    [Fact]
    public void AppendToIndex_ExistingIndex_AppendsRows()
    {
        var existing = """
            # Research Index

            | JD Number | Title | Path |
            |-----------|-------|------|
            | 10.01 | First | 10/10.01-first.md |
            """;

        var result = ResearchArticleWriter.AppendToIndex(existing,
            [("20.01", "Second", "20/20.01-second.md")]);

        result.ShouldContain("| 10.01 | First | 10/10.01-first.md |");
        result.ShouldContain("| 20.01 | Second | 20/20.01-second.md |");
    }

    [Fact]
    public void AppendToIndex_MultipleEntries_SortedByJdNumber()
    {
        var result = ResearchArticleWriter.AppendToIndex("",
            [("20.01", "B", "20/b.md"), ("10.01", "A", "10/a.md")]);

        var indexOfA = result.IndexOf("10.01", StringComparison.Ordinal);
        var indexOfB = result.IndexOf("20.01", StringComparison.Ordinal);
        indexOfA.ShouldBeLessThan(indexOfB);
    }

    #endregion

    #region WriteArticlesAsync

    [Fact]
    public async Task WriteArticlesAsync_NoKeptItems_ReturnsEmptyResult()
    {
        var output = new ArchivistOutput
        {
            Items =
            [
                new ArchivistCurationItem
                {
                    Disposition = "discard",
                    Rationale = "Not relevant",
                    SourceRefs = ["src/test.cs"],
                },
            ],
            Summary = "All discarded",
        };

        _storage.ReadAsync("INDEX.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var writer = new ResearchArticleWriter(_storage);
        var result = await writer.WriteArticlesAsync(output, 3135);

        result.Articles.ShouldBeEmpty();
        result.IndexUpdated.ShouldBeFalse();
        result.TotalKept.ShouldBe(0);
        result.TotalDiscarded.ShouldBe(1);
        result.TotalExpand.ShouldBe(0);
    }

    [Fact]
    public async Task WriteArticlesAsync_KeptItem_WritesArticleAndIndex()
    {
        var output = new ArchivistOutput
        {
            Items =
            [
                new ArchivistCurationItem
                {
                    Disposition = "keep",
                    Rationale = "Valuable pattern",
                    SourceRefs = ["src/Example.cs"],
                    Article = new ArchivistArticle
                    {
                        Title = "Test Pattern",
                        BodyMarkdown = "Article body content.",
                        Category = "10",
                        Topics = ["testing"],
                    },
                },
            ],
        };

        // INDEX.md doesn't exist yet.
        _storage.ReadAsync("INDEX.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        // Category directory empty (no prior articles).
        _storage.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));

        var writer = new ResearchArticleWriter(_storage);
        var result = await writer.WriteArticlesAsync(output, 3135);

        result.Articles.Count.ShouldBe(1);
        result.Articles[0].JdNumber.ShouldBe("10.01");
        result.Articles[0].Title.ShouldBe("Test Pattern");
        result.Articles[0].Path.ShouldBe("10/10.01-test-pattern.md");
        result.IndexUpdated.ShouldBeTrue();
        result.TotalKept.ShouldBe(1);

        // Verify article was written.
        await _storage.Received(1).WriteAsync(
            "10/10.01-test-pattern.md",
            Arg.Is<string>(c => c.Contains("title: \"Test Pattern\"") && c.Contains("Article body content.")),
            Arg.Is<string>(m => m.Contains("AB#3135")),
            Arg.Any<CancellationToken>());

        // Verify INDEX.md was written.
        await _storage.Received(1).WriteAsync(
            "INDEX.md",
            Arg.Is<string>(c => c.Contains("10.01") && c.Contains("Test Pattern")),
            Arg.Is<string>(m => m.Contains("INDEX.md")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteArticlesAsync_IdempotentRerun_SkipsExistingArticle()
    {
        var output = new ArchivistOutput
        {
            Items =
            [
                new ArchivistCurationItem
                {
                    Disposition = "keep",
                    Rationale = "Valuable",
                    SourceRefs = ["src/a.cs"],
                    Article = new ArchivistArticle
                    {
                        Title = "Existing Article",
                        BodyMarkdown = "Body.",
                        Category = "10",
                    },
                },
            ],
        };

        // INDEX.md already has this entry.
        var existingIndex = "# Research Index\n\n| JD Number | Title | Path |\n|-----------|-------|------|\n| 10.01 | Existing Article | 10/10.01-existing-article.md |\n";
        _storage.ReadAsync("INDEX.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(existingIndex));

        // Category directory listing reveals the existing article file.
        _storage.ListAsync("10", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["10.01-existing-article.md"]));

        var writer = new ResearchArticleWriter(_storage);
        var result = await writer.WriteArticlesAsync(output, 3135);

        result.Articles.Count.ShouldBe(1);
        result.Articles[0].JdNumber.ShouldBe("10.01");
        result.Articles[0].Path.ShouldBe("10/10.01-existing-article.md");
        result.IndexUpdated.ShouldBeFalse(); // Already in index.

        // Article should NOT have been written again.
        await _storage.DidNotReceive().WriteAsync(
            Arg.Is<string>(p => p.Contains("existing-article")),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteArticlesAsync_MixedDispositions_CountsCorrectly()
    {
        var output = new ArchivistOutput
        {
            Items =
            [
                new ArchivistCurationItem
                {
                    Disposition = "keep",
                    Rationale = "Good",
                    SourceRefs = ["a.cs"],
                    Article = new ArchivistArticle
                    {
                        Title = "Kept",
                        BodyMarkdown = "Body.",
                        Category = "10",
                    },
                },
                new ArchivistCurationItem
                {
                    Disposition = "discard",
                    Rationale = "Not useful",
                    SourceRefs = ["b.cs"],
                },
                new ArchivistCurationItem
                {
                    Disposition = "expand",
                    Rationale = "Needs more work",
                    SourceRefs = ["c.cs"],
                },
                new ArchivistCurationItem
                {
                    Disposition = "discard",
                    Rationale = "Also not useful",
                    SourceRefs = ["d.cs"],
                },
            ],
        };

        _storage.ReadAsync("INDEX.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        _storage.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));

        var writer = new ResearchArticleWriter(_storage);
        var result = await writer.WriteArticlesAsync(output, 100);

        result.TotalKept.ShouldBe(1);
        result.TotalDiscarded.ShouldBe(2);
        result.TotalExpand.ShouldBe(1);
    }

    [Fact]
    public async Task WriteArticlesAsync_ArticleWrittenButIndexMissing_RepairsIndex()
    {
        var output = new ArchivistOutput
        {
            Items =
            [
                new ArchivistCurationItem
                {
                    Disposition = "keep",
                    Rationale = "Good",
                    SourceRefs = ["a.cs"],
                    Article = new ArchivistArticle
                    {
                        Title = "Repaired",
                        BodyMarkdown = "Body.",
                        Category = "10",
                    },
                },
            ],
        };

        // INDEX.md exists but does NOT have this entry.
        var existingIndex = "# Research Index\n\n| JD Number | Title | Path |\n|-----------|-------|------|\n";
        _storage.ReadAsync("INDEX.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(existingIndex));
        // Category listing reveals article exists from a prior partial run.
        _storage.ListAsync("10", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["10.01-repaired.md"]));

        var writer = new ResearchArticleWriter(_storage);
        var result = await writer.WriteArticlesAsync(output, 100);

        // Article was skipped (already exists), but index should be updated.
        result.IndexUpdated.ShouldBeTrue();
        await _storage.Received(1).WriteAsync(
            "INDEX.md",
            Arg.Is<string>(c => c.Contains("10.01") && c.Contains("Repaired")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteArticlesAsync_KeepWithNullArticle_SkipsItem()
    {
        var output = new ArchivistOutput
        {
            Items =
            [
                new ArchivistCurationItem
                {
                    Disposition = "keep",
                    Rationale = "Valuable but no article",
                    SourceRefs = ["a.cs"],
                    Article = null, // malformed — keep without article
                },
            ],
        };

        _storage.ReadAsync("INDEX.md", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var writer = new ResearchArticleWriter(_storage);
        var result = await writer.WriteArticlesAsync(output, 100);

        result.Articles.ShouldBeEmpty();
        result.TotalKept.ShouldBe(0);
    }

    #endregion
}
