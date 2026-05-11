using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Models;
using Polyphony.Research;
using Polyphony.Tests.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Research;

/// <summary>
/// Tests for <c>polyphony research promote</c> — the promotion writer that
/// reads archivist decisions and writes kept artifacts via
/// <see cref="IResearchStore"/>.
/// </summary>
public sealed class ResearchPromoteCommandTests : CommandTestBase, IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryResearchStore _store;

    public ResearchPromoteCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-promote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new InMemoryResearchStore();
    }

    public new void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
        base.Dispose();
    }

    private ResearchCommands CreateCommand() => new(_store);

    private static ResearchPromoteResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResearchPromoteResult)!;

    private static string MakeDecisionsJson(params ArchivistDecision[] decisions) =>
        JsonSerializer.Serialize(decisions.ToList(), PolyphonyJsonContext.Default.ListArchivistDecision);

    private static string MakeDestinationJson(string platform = "azure_devops") =>
        JsonSerializer.Serialize(new ResearchDestination
        {
            Platform = platform,
            RepoLocator = "polyphonyrequiem/Polyphony/research",
            Branch = "main",
            RootPath = "articles",
        }, PolyphonyJsonContext.Default.ResearchDestination);

    private static ArchivistDecision MakeDecision(string path, string decision) =>
        new()
        {
            ArtifactPath = path,
            Decision = decision,
            Rationale = $"Test rationale for {decision}",
            RelevanceSignals = new RelevanceSignals
            {
                Domain = "high",
                Codebase = "medium",
                TechnologyStacks = "high",
                Ecosystem = "low",
                Linkability = "medium",
            },
        };

    // ── Missing required args ─────────────────────────────────────────────
    [Fact]
    public async Task MissingArgs_EmitsRoutingError()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() => cmd.Promote());

        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Verb.ShouldBe("research promote");
    }

    // ── Keep: writes to store with citation ───────────────────────────────
    [Fact]
    public async Task KeepDecision_WritesToStoreWithCitation()
    {
        File.WriteAllText(Path.Combine(_tempDir, "article.md"), "# Research Article\nSome content.");
        var decisions = MakeDecisionsJson(MakeDecision("article.md", CurationDecision.Keep));
        var destination = MakeDestinationJson();

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(42, _tempDir, decisions, destination));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Promoted.ShouldContain("article.md");
        result.DiscardedCount.ShouldBe(0);
        result.ExpandRequested.ShouldBeEmpty();

        // Verify content was written to store with citation front matter
        _store.Files.ShouldContainKey("articles/article.md");
        var written = _store.Files["articles/article.md"];
        written.ShouldContain("source_url:");
        written.ShouldContain("capture_date:");
        written.ShouldContain("freshness:");
        written.ShouldContain("# Research Article");
    }

    // ── Discard: no write ─────────────────────────────────────────────────
    [Fact]
    public async Task DiscardDecision_DoesNotWrite()
    {
        File.WriteAllText(Path.Combine(_tempDir, "noise.md"), "irrelevant");
        var decisions = MakeDecisionsJson(MakeDecision("noise.md", CurationDecision.Discard));
        var destination = MakeDestinationJson();

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(42, _tempDir, decisions, destination));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Promoted.ShouldBeEmpty();
        result.DiscardedCount.ShouldBe(1);
        _store.Files.ShouldBeEmpty();
    }

    // ── Expand: recorded for loop-back ────────────────────────────────────
    [Fact]
    public async Task ExpandDecision_RecordedForLoopBack()
    {
        File.WriteAllText(Path.Combine(_tempDir, "promising.md"), "needs more");
        var decisions = MakeDecisionsJson(MakeDecision("promising.md", CurationDecision.Expand));
        var destination = MakeDestinationJson();

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(42, _tempDir, decisions, destination));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Promoted.ShouldBeEmpty();
        result.ExpandRequested.ShouldContain("promising.md");
        result.DiscardedCount.ShouldBe(0);
        _store.Files.ShouldBeEmpty();
    }

    // ── Mixed decisions ───────────────────────────────────────────────────
    [Fact]
    public async Task MixedDecisions_HandlesAllBranches()
    {
        File.WriteAllText(Path.Combine(_tempDir, "keep.md"), "# Keep");
        File.WriteAllText(Path.Combine(_tempDir, "discard.md"), "drop");
        File.WriteAllText(Path.Combine(_tempDir, "expand.md"), "dig deeper");

        var decisions = MakeDecisionsJson(
            MakeDecision("keep.md", CurationDecision.Keep),
            MakeDecision("discard.md", CurationDecision.Discard),
            MakeDecision("expand.md", CurationDecision.Expand));
        var destination = MakeDestinationJson();

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(99, _tempDir, decisions, destination));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Promoted.ShouldBe(["keep.md"]);
        result.ExpandRequested.ShouldBe(["expand.md"]);
        result.DiscardedCount.ShouldBe(1);
        result.PlatformCombo.ShouldBe("source:github+research:azure_devops");
    }

    // ── Citation enrichment: front matter prepended ───────────────────────
    [Fact]
    public async Task CitationEnrichment_PrependsFrontMatter()
    {
        File.WriteAllText(Path.Combine(_tempDir, "article.md"), "# Title\nBody text.");
        var decisions = MakeDecisionsJson(MakeDecision("article.md", CurationDecision.Keep));
        var destination = MakeDestinationJson();

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(42, _tempDir, decisions, destination));

        exit.ShouldBe(ExitCodes.Success);
        var written = _store.Files["articles/article.md"];

        // Verify YAML front matter structure
        written.ShouldStartWith("---\n");
        written.ShouldContain("source_url: \"unknown\"");
        written.ShouldContain("capture_date:");
        written.ShouldContain("freshness: \"fresh\"");
        written.ShouldContain("\n---\n");
        written.ShouldContain("# Title");
        written.ShouldContain("Body text.");
    }

    // ── Citation enrichment: preserves existing source_url ─────────────────
    [Fact]
    public async Task CitationEnrichment_PreservesExistingSourceUrl()
    {
        var content = "---\nsource_url: \"https://example.com/article\"\n---\n# Title\nBody text.\n";
        File.WriteAllText(Path.Combine(_tempDir, "article.md"), content);
        var decisions = MakeDecisionsJson(MakeDecision("article.md", CurationDecision.Keep));
        var destination = MakeDestinationJson();

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(42, _tempDir, decisions, destination));

        exit.ShouldBe(ExitCodes.Success);
        var written = _store.Files["articles/article.md"];
        written.ShouldContain("source_url: \"https://example.com/article\"");
    }

    // ── Platform combo recorded ───────────────────────────────────────────
    [Fact]
    public async Task PlatformCombo_RecordedInResult()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.md"), "content");
        var decisions = MakeDecisionsJson(MakeDecision("a.md", CurationDecision.Keep));
        var destination = MakeDestinationJson("azure_devops");

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(42, _tempDir, decisions, destination));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.PlatformCombo.ShouldBe("source:github+research:azure_devops");
    }

    // ── Missing scratch file ──────────────────────────────────────────────
    [Fact]
    public async Task MissingScratchFile_EmitsError()
    {
        var decisions = MakeDecisionsJson(MakeDecision("missing.md", CurationDecision.Keep));
        var destination = MakeDestinationJson();

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(42, _tempDir, decisions, destination));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("scratch_file_missing");
    }

    // ── Invalid destination JSON ──────────────────────────────────────────
    [Fact]
    public async Task InvalidDestinationJson_EmitsError()
    {
        var decisions = MakeDecisionsJson(MakeDecision("a.md", CurationDecision.Keep));

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(42, _tempDir, decisions, "not-json"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_destination_json");
    }

    // ── Invalid decisions JSON ────────────────────────────────────────────
    [Fact]
    public async Task InvalidDecisionsJson_EmitsError()
    {
        var destination = MakeDestinationJson();

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(42, _tempDir, "not-valid-json", destination));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_decisions_json");
    }

    // ── Invalid decision value ────────────────────────────────────────────
    [Fact]
    public async Task InvalidDecisionValue_EmitsError()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.md"), "content");
        var decisions = MakeDecisionsJson(MakeDecision("a.md", "bogus"));
        var destination = MakeDestinationJson();

        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.Promote(42, _tempDir, decisions, destination));

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.ErrorCode.ShouldBe("invalid_decision_value");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("bogus");
    }
}
