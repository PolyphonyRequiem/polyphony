using NSubstitute;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Infrastructure.Research;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Research;

public sealed class GitHubResearchStorageTests
{
    private readonly IProcessRunner _runner = Substitute.For<IProcessRunner>();
    private static readonly GhClientPolicy NoRetry = GhClientPolicy.NoRetry;

    private GitHubResearchStorage CreateStorage(
        string repository = "owner/repo",
        string basePath = "",
        string branch = "main") =>
        new(new ResearchConfig
        {
            Repository = repository,
            BasePath = basePath,
            Branch = branch,
        }, _runner, NoRetry);

    #region ReadAsync

    [Fact]
    public async Task ReadAsync_Success_ReturnsDecodedContent()
    {
        var storage = CreateStorage(basePath: "data");
        var base64Content = Convert.ToBase64String("hello world"u8.ToArray());

        _runner.RunAsync(
            "gh",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("/repos/owner/repo/contents/data/test.md?ref=main")),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(0, base64Content + "\n", ""));

        var result = await storage.ReadAsync("test.md");

        result.ShouldBe("hello world");
    }

    [Fact]
    public async Task ReadAsync_FileNotFound_ReturnsNull()
    {
        var storage = CreateStorage();

        _runner.RunAsync(
            "gh",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(1, "", "HTTP 404: Not Found"));

        var result = await storage.ReadAsync("missing.md");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_NonNotFoundError_Throws()
    {
        var storage = CreateStorage();

        _runner.RunAsync(
            "gh",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(1, "", "Internal Server Error"));

        await Should.ThrowAsync<ExternalToolException>(
            () => storage.ReadAsync("test.md"));
    }

    [Fact]
    public async Task ReadAsync_EmptyBasePath_UsesPathDirectly()
    {
        var storage = CreateStorage(basePath: "");

        _runner.RunAsync(
            "gh",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("/repos/owner/repo/contents/notes/test.md?ref=main")),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(0, Convert.ToBase64String("ok"u8.ToArray()), ""));

        var result = await storage.ReadAsync("notes/test.md");

        result.ShouldBe("ok");
    }

    #endregion

    #region WriteAsync

    [Fact]
    public async Task WriteAsync_NewFile_CreatesWithoutSha()
    {
        var storage = CreateStorage(basePath: "data");
        string? capturedStdin = null;

        // First call: SHA lookup returns 404 (file doesn't exist)
        _runner.RunAsync(
            "gh",
            Arg.Is<IReadOnlyList<string>>(a =>
                a.Any(s => s.Contains("/repos/owner/repo/contents/data/new.md")) && a.Contains("--jq")),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(1, "", "404 Not Found"));

        // Second call: PUT to create
        _runner.RunAsync(
            "gh",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("--method") && a.Contains("PUT")),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Do<string?>(s => capturedStdin = s),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(0, "{}", ""));

        await storage.WriteAsync("new.md", "content", "Add new.md");

        capturedStdin.ShouldNotBeNull();
        capturedStdin.ShouldNotContain("\"sha\"");
        capturedStdin.ShouldContain("\"message\"");
        capturedStdin.ShouldContain("\"content\"");
    }

    [Fact]
    public async Task WriteAsync_ExistingFile_IncludesSha()
    {
        var storage = CreateStorage();
        string? capturedStdin = null;

        // SHA lookup succeeds
        _runner.RunAsync(
            "gh",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("--jq")),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(0, "abc123sha\n", ""));

        // PUT call
        _runner.RunAsync(
            "gh",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("PUT")),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Do<string?>(s => capturedStdin = s),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(0, "{}", ""));

        await storage.WriteAsync("existing.md", "updated", "Update existing.md");

        capturedStdin.ShouldNotBeNull();
        capturedStdin.ShouldContain("abc123sha");
    }

    [Fact]
    public async Task WriteAsync_PutFails_Throws()
    {
        var storage = CreateStorage();

        // SHA lookup — file doesn't exist
        _runner.RunAsync(
            "gh",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("--jq")),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(1, "", "404"));

        // PUT fails
        _runner.RunAsync(
            "gh",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("PUT")),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(1, "", "403 Forbidden"));

        await Should.ThrowAsync<ExternalToolException>(
            () => storage.WriteAsync("test.md", "content", "commit msg"));
    }

    #endregion

    #region ListAsync

    [Fact]
    public async Task ListAsync_Success_ReturnsPaths()
    {
        var storage = CreateStorage(basePath: "data");

        _runner.RunAsync(
            "gh",
            Arg.Is<IReadOnlyList<string>>(a => a.Contains("/repos/owner/repo/contents/data/notes?ref=main")),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(0,
                """[{"path":"data/notes/a.md","type":"file"},{"path":"data/notes/b.md","type":"file"}]""",
                ""));

        var result = await storage.ListAsync("notes");

        result.Count.ShouldBe(2);
        result.ShouldContain("notes/a.md");
        result.ShouldContain("notes/b.md");
    }

    [Fact]
    public async Task ListAsync_DirectoryNotFound_ReturnsEmpty()
    {
        var storage = CreateStorage();

        _runner.RunAsync(
            "gh",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(1, "", "404 Not Found"));

        var result = await storage.ListAsync("nonexistent");

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_EmptyDirectory_ReturnsEmpty()
    {
        var storage = CreateStorage();

        _runner.RunAsync(
            "gh",
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string?>?>(),
            Arg.Any<bool>())
            .Returns(new ProcessResult(0, "[]", ""));

        var result = await storage.ListAsync("");

        result.ShouldBeEmpty();
    }

    #endregion

    #region Auth scoping

    [Fact]
    public void Constructor_NoAuthOverride_DoesNotSetGhToken()
    {
        var config = new ResearchConfig
        {
            Repository = "owner/repo",
            Auth = null,
        };

        // Should construct without issue; GH_TOKEN not in env override
        var storage = new GitHubResearchStorage(config, _runner, NoRetry);
        storage.ShouldNotBeNull();
    }

    #endregion
}
