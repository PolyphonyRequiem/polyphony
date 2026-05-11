using NSubstitute;
using Polyphony.Configuration;
using Polyphony.Research;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Research;

public sealed class AdoResearchStoreTests
{
    private readonly IAdoResearchClient _client = Substitute.For<IAdoResearchClient>();
    private readonly IResearchStore _store;

    public AdoResearchStoreTests()
    {
        var config = new EffectiveResearchConfig(
            Repository: "MyOrg/MyProject/research-repo",
            Platform: "ado",
            DefaultBranch: "main");
        var factory = new ResearchStoreFactory();
        _store = factory.Create(config, adoClient: _client)!;
    }

    // ──────────────────────────────────────────────────────────────────────
    // ReadAsync
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_FileExists_ReturnsEntry()
    {
        _client.GetFileContentAsync(
            "MyOrg", "MyProject", "research-repo", "docs/analysis.md", "main",
            Arg.Any<CancellationToken>())
            .Returns(new AdoFileContent("docs/analysis.md", "# ADO research"));

        var result = await _store.ReadAsync("docs/analysis.md");

        result.ShouldNotBeNull();
        result!.Path.ShouldBe("docs/analysis.md");
        result.Content.ShouldBe("# ADO research");
    }

    [Fact]
    public async Task ReadAsync_FileNotFound_ReturnsNull()
    {
        _client.GetFileContentAsync(
            "MyOrg", "MyProject", "research-repo", "missing.md", "main",
            Arg.Any<CancellationToken>())
            .Returns((AdoFileContent?)null);

        var result = await _store.ReadAsync("missing.md");

        result.ShouldBeNull();
    }

    // ──────────────────────────────────────────────────────────────────────
    // WriteAsync
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_Success_ReturnsCommitSha()
    {
        _client.PushFileContentAsync(
            "MyOrg", "MyProject", "research-repo",
            "output/result.md", "content", "Commit msg", "main",
            Arg.Any<CancellationToken>())
            .Returns(new AdoWriteResponse(true, "adoSha789", null));

        var result = await _store.WriteAsync("output/result.md", "content", "Commit msg");

        result.Success.ShouldBeTrue();
        result.CommitSha.ShouldBe("adoSha789");
    }

    [Fact]
    public async Task WriteAsync_Failure_ReturnsError()
    {
        _client.PushFileContentAsync(
            "MyOrg", "MyProject", "research-repo",
            "output/result.md", "content", "Commit msg", "main",
            Arg.Any<CancellationToken>())
            .Returns(new AdoWriteResponse(false, null, "Permission denied"));

        var result = await _store.WriteAsync("output/result.md", "content", "Commit msg");

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("Permission denied");
    }

    // ──────────────────────────────────────────────────────────────────────
    // ListAsync
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ItemsExist_ReturnsEntries()
    {
        _client.ListItemsAsync(
            "MyOrg", "MyProject", "research-repo", "data/", "main",
            Arg.Any<CancellationToken>())
            .Returns(new[] { "data/file1.md", "data/file2.md" });

        var result = await _store.ListAsync("data/");

        result.ShouldNotBeEmpty();
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ListAsync_NoItems_ReturnsEmpty()
    {
        _client.ListItemsAsync(
            "MyOrg", "MyProject", "research-repo", "empty/", "main",
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        var result = await _store.ListAsync("empty/");

        result.ShouldBeEmpty();
    }
}
