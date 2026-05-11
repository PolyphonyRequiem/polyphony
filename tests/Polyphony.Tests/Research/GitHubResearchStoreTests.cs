using NSubstitute;
using Polyphony.Configuration;
using Polyphony.Research;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Research;

public sealed class GitHubResearchStoreTests
{
    private readonly IGitHubResearchClient _client = Substitute.For<IGitHubResearchClient>();
    private readonly IResearchStore _store;

    public GitHubResearchStoreTests()
    {
        var config = new EffectiveResearchConfig(
            Repository: "PolyphonyRequiem/polyphony-research",
            Platform: "github",
            DefaultBranch: "main");
        var factory = new ResearchStoreFactory();
        _store = factory.Create(config, gitHubClient: _client)!;
    }

    // ──────────────────────────────────────────────────────────────────────
    // ReadAsync
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_FileExists_ReturnsEntry()
    {
        _client.GetFileContentAsync(
            "PolyphonyRequiem", "polyphony-research", "notes/topic.md", "main",
            Arg.Any<CancellationToken>())
            .Returns(new GitHubFileContent("notes/topic.md", "# Research notes", "abc123"));

        var result = await _store.ReadAsync("notes/topic.md");

        result.ShouldNotBeNull();
        result!.Path.ShouldBe("notes/topic.md");
        result.Content.ShouldBe("# Research notes");
    }

    [Fact]
    public async Task ReadAsync_FileNotFound_ReturnsNull()
    {
        _client.GetFileContentAsync(
            "PolyphonyRequiem", "polyphony-research", "nonexistent.md", "main",
            Arg.Any<CancellationToken>())
            .Returns((GitHubFileContent?)null);

        var result = await _store.ReadAsync("nonexistent.md");

        result.ShouldBeNull();
    }

    // ──────────────────────────────────────────────────────────────────────
    // WriteAsync
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_NewFile_CreatesWithNullSha()
    {
        _client.GetFileContentAsync(
            "PolyphonyRequiem", "polyphony-research", "new-file.md", "main",
            Arg.Any<CancellationToken>())
            .Returns((GitHubFileContent?)null);

        _client.PutFileContentAsync(
            "PolyphonyRequiem", "polyphony-research", "new-file.md",
            "content", "Add research", "main", null,
            Arg.Any<CancellationToken>())
            .Returns(new GitHubWriteResponse(true, "sha456", null));

        var result = await _store.WriteAsync("new-file.md", "content", "Add research");

        result.Success.ShouldBeTrue();
        result.CommitSha.ShouldBe("sha456");
    }

    [Fact]
    public async Task WriteAsync_ExistingFile_PassesExistingSha()
    {
        _client.GetFileContentAsync(
            "PolyphonyRequiem", "polyphony-research", "existing.md", "main",
            Arg.Any<CancellationToken>())
            .Returns(new GitHubFileContent("existing.md", "old content", "existingSha"));

        _client.PutFileContentAsync(
            "PolyphonyRequiem", "polyphony-research", "existing.md",
            "new content", "Update research", "main", "existingSha",
            Arg.Any<CancellationToken>())
            .Returns(new GitHubWriteResponse(true, "newSha", null));

        var result = await _store.WriteAsync("existing.md", "new content", "Update research");

        result.Success.ShouldBeTrue();
        result.CommitSha.ShouldBe("newSha");
    }

    // ──────────────────────────────────────────────────────────────────────
    // ListAsync
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_DirectoryExists_ReturnsEntries()
    {
        _client.ListDirectoryAsync(
            "PolyphonyRequiem", "polyphony-research", "notes/", "main",
            Arg.Any<CancellationToken>())
            .Returns(new[] { "topic-a.md", "topic-b.md" });

        var result = await _store.ListAsync("notes/");

        result.ShouldNotBeEmpty();
        result.Count.ShouldBe(2);
        result.ShouldContain("topic-a.md");
    }

    [Fact]
    public async Task ListAsync_DirectoryNotFound_ReturnsEmpty()
    {
        _client.ListDirectoryAsync(
            "PolyphonyRequiem", "polyphony-research", "nonexistent/", "main",
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        var result = await _store.ListAsync("nonexistent/");

        result.ShouldBeEmpty();
    }
}
