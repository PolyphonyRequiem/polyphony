using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Models;
using Polyphony.Research;
using Polyphony.Tests.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Research;

/// <summary>
/// End-to-end integration test proving the cross-platform write path for
/// AB#3082. Drives archivist (curate) → promotion writer (promote) through
/// a non-<c>github↔github</c> platform combination.
///
/// <para><b>Chosen combo:</b> source-on-GitHub + research-on-ADO
/// (<c>"source:github+research:azure_devops"</c>). Selected because the
/// InMemoryResearchStore can simulate the ADO target without live network
/// calls, and this is the combo specified as tentative in the parent Issue.</para>
///
/// <para>This test runs deterministically in CI — no live network calls,
/// no external process dependencies. The platform selection is exercised
/// through the <see cref="ResearchDestination"/> routing, not per-test
/// platform-specific bypasses.</para>
/// </summary>
public sealed class CrossPlatformWriteProofTests : CommandTestBase, IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryResearchStore _store;

    /// <summary>
    /// The platform combination under test: source repo on GitHub,
    /// research (sibling) repo on Azure DevOps.
    /// </summary>
    private const string ProvenCombo = "source:github+research:azure_devops";

    public CrossPlatformWriteProofTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-xplat-{Guid.NewGuid():N}");
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

    private static ResearchCurateResult ParseCurate(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResearchCurateResult)!;

    private static ResearchPromoteResult ParsePromote(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ResearchPromoteResult)!;

    /// <summary>
    /// End-to-end: curate scratch → promote kept artifacts to ADO sibling repo.
    /// Proves the full archivist → promotion writer pipeline for a cross-platform
    /// (source:GitHub + research:ADO) combination.
    /// </summary>
    [Fact]
    public async Task EndToEnd_CurateThenPromote_CrossPlatformWriteProof()
    {
        // ── Arrange: seed scratch directory with 3 artifacts ──────────────
        File.WriteAllText(
            Path.Combine(_tempDir, "api-versioning.md"),
            "---\nsource_url: \"https://docs.example.com/api-versioning\"\ncapture_date: \"2026-05-11T10:00:00Z\"\n---\n# API Versioning Best Practices\nContent about versioning strategies.");

        File.WriteAllText(
            Path.Combine(_tempDir, "irrelevant-meme.md"),
            "# Not Useful\nThis is noise.");

        File.WriteAllText(
            Path.Combine(_tempDir, "event-sourcing.md"),
            "---\nsource_url: \"https://example.com/event-sourcing\"\n---\n# Event Sourcing Patterns\nNeeds deeper analysis of CQRS integration.");

        // Build archivist decisions (normally produced by the LLM agent)
        var decisions = new List<ArchivistDecision>
        {
            new()
            {
                ArtifactPath = "api-versioning.md",
                Decision = CurationDecision.Keep,
                Rationale = "Directly relevant to API design patterns in the codebase.",
                RelevanceSignals = new RelevanceSignals
                {
                    Domain = "high",
                    Codebase = "high",
                    TechnologyStacks = "medium",
                    Ecosystem = "high",
                    Linkability = "high",
                },
            },
            new()
            {
                ArtifactPath = "irrelevant-meme.md",
                Decision = CurationDecision.Discard,
                Rationale = "No relevance to domain or technology stack.",
                RelevanceSignals = new RelevanceSignals
                {
                    Domain = "none",
                    Codebase = "none",
                    TechnologyStacks = "none",
                    Ecosystem = "none",
                    Linkability = "none",
                },
            },
            new()
            {
                ArtifactPath = "event-sourcing.md",
                Decision = CurationDecision.Expand,
                Rationale = "Promising but needs CQRS integration analysis.",
                RelevanceSignals = new RelevanceSignals
                {
                    Domain = "medium",
                    Codebase = "low",
                    TechnologyStacks = "high",
                    Ecosystem = "medium",
                    Linkability = "medium",
                },
            },
        };

        var decisionsJson = JsonSerializer.Serialize(
            decisions, PolyphonyJsonContext.Default.ListArchivistDecision);

        // Research destination: ADO sibling repo (non-github target)
        var destination = new ResearchDestination
        {
            Platform = "azure_devops",
            RepoLocator = "polyphonyrequiem/Polyphony/research",
            Branch = "main",
            RootPath = "articles",
        };
        var destinationJson = JsonSerializer.Serialize(
            destination, PolyphonyJsonContext.Default.ResearchDestination);

        var cmd = CreateCommand();

        // ── Step 1: Curate ────────────────────────────────────────────────
        var (curateExit, curateOutput) = CaptureConsole(() =>
            cmd.Curate(3075, _tempDir, decisionsJson));

        curateExit.ShouldBe(ExitCodes.Success);
        var curateResult = ParseCurate(curateOutput);
        curateResult.Error.ShouldBeNull();
        curateResult.ArtifactCount.ShouldBe(3);
        curateResult.Decisions.Count.ShouldBe(3);

        // ── Step 2: Promote ───────────────────────────────────────────────
        var (promoteExit, promoteOutput) = await CaptureConsoleAsync(() =>
            cmd.Promote(3075, _tempDir, decisionsJson, destinationJson));

        promoteExit.ShouldBe(ExitCodes.Success);
        var promoteResult = ParsePromote(promoteOutput);
        promoteResult.Error.ShouldBeNull();

        // ── Assert: cross-platform combo recorded ─────────────────────────
        promoteResult.PlatformCombo.ShouldBe(ProvenCombo);

        // ── Assert: keep artifact promoted with citation ──────────────────
        promoteResult.Promoted.ShouldBe(["api-versioning.md"]);
        _store.Files.ShouldContainKey("articles/api-versioning.md");
        var promoted = _store.Files["articles/api-versioning.md"];
        promoted.ShouldContain("source_url: \"https://docs.example.com/api-versioning\"");
        promoted.ShouldContain("capture_date:");
        promoted.ShouldContain("freshness:");
        promoted.ShouldContain("# API Versioning Best Practices");

        // ── Assert: discard produced no write ─────────────────────────────
        promoteResult.DiscardedCount.ShouldBe(1);
        _store.Files.Keys.ShouldNotContain(k => k.Contains("irrelevant-meme"));

        // ── Assert: expand recorded for loop-back (#3076) ─────────────────
        promoteResult.ExpandRequested.ShouldBe(["event-sourcing.md"]);
        _store.Files.Keys.ShouldNotContain(k => k.Contains("event-sourcing"));
    }
}
