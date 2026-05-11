using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// End-to-end tests for <c>polyphony agent archivist</c>. Verifies
/// the routing-style envelope (always exit 0; <c>error_code</c> categorical
/// for failures), scratch directory enumeration, deterministic artifact
/// ordering, and the snake_case JSON contract.
/// </summary>
public sealed class AgentCommandsArchivistTests : CommandTestBase
{
    private AgentCommands CreateCommand() => new(Repository, Config);

    [Fact]
    public async Task Archivist_ValidScratchDir_EnumeratesArtifacts()
    {
        var tempDir = CreateScratchDir(3071, "notes/design.md", "research/auth.md", "README.md");
        try
        {
            var cmd = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => cmd.Archivist(3071, tempDir));

            exitCode.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.ArchivistResult);
            result.ShouldNotBeNull();
            result.Apex.ShouldBe(3071);
            result.Decisions.Count.ShouldBe(3);
            result.Error.ShouldBeNull();
            result.ErrorCode.ShouldBeNull();

            // Verify deterministic ordinal sort order.
            result.Decisions[0].Artifact.ShouldBe("README.md");
            result.Decisions[1].Artifact.ShouldBe("notes/design.md");
            result.Decisions[2].Artifact.ShouldBe("research/auth.md");
        }
        finally { CleanupTempDir(tempDir); }
    }

    [Fact]
    public async Task Archivist_ArtifactPaths_AreRelativeWithForwardSlashes()
    {
        var tempDir = CreateScratchDir(42, "sub/nested/file.txt");
        try
        {
            var cmd = CreateCommand();
            var (_, output) = await CaptureConsoleAsync(
                () => cmd.Archivist(42, tempDir));

            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.ArchivistResult);
            result!.Decisions[0].Artifact.ShouldBe("sub/nested/file.txt");
            result.Decisions[0].Artifact.ShouldNotContain("\\");
        }
        finally { CleanupTempDir(tempDir); }
    }

    [Fact]
    public async Task Archivist_ScratchDirNotFound_RoutingEnvelope()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(
            () => cmd.Archivist(9999, "nonexistent/path"));

        exitCode.ShouldBe(ExitCodes.Success);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error_code").GetString().ShouldBe("scratch_dir_not_found");
        doc.RootElement.GetProperty("apex").GetInt32().ShouldBe(9999);
    }

    [Fact]
    public async Task Archivist_EmptyDir_NoArtifacts_RoutingEnvelope()
    {
        var tempDir = CreateScratchDir(7777); // No files.
        try
        {
            var cmd = CreateCommand();
            var (exitCode, output) = await CaptureConsoleAsync(
                () => cmd.Archivist(7777, tempDir));

            exitCode.ShouldBe(ExitCodes.Success);
            var doc = JsonDocument.Parse(output);
            doc.RootElement.GetProperty("error_code").GetString().ShouldBe("no_artifacts");
        }
        finally { CleanupTempDir(tempDir); }
    }

    [Fact]
    public async Task Archivist_InvalidApex_RoutingEnvelope()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(
            () => cmd.Archivist(0));

        exitCode.ShouldBe(ExitCodes.Success);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error_code").GetString().ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task Archivist_NegativeApex_RoutingEnvelope()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureConsoleAsync(
            () => cmd.Archivist(-1));

        exitCode.ShouldBe(ExitCodes.Success);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("error_code").GetString().ShouldBe("invalid_argument");
    }

    [Fact]
    public async Task Archivist_JsonContract_SnakeCaseKeys()
    {
        var tempDir = CreateScratchDir(1234, "file.md");
        try
        {
            var cmd = CreateCommand();
            var (_, output) = await CaptureConsoleAsync(
                () => cmd.Archivist(1234, tempDir));

            output.ShouldContain("\"apex\":");
            output.ShouldContain("\"scratch_path\":");
            output.ShouldContain("\"decisions\":");
            output.ShouldContain("\"artifact\":");
            output.ShouldContain("\"relevance_signals\":");
            output.ShouldContain("\"technology_stacks\":");

            // Must NOT leak PascalCase property names. Only check multi-word
            // properties where PascalCase differs from snake_case.
            output.ShouldNotContain("ScratchPath");
            output.ShouldNotContain("RelevanceSignals");
            output.ShouldNotContain("TechnologyStacks");
        }
        finally { CleanupTempDir(tempDir); }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Creates a temp scratch root with a per-apex subdirectory and the
    /// given relative file paths. Returns the scratch root path (caller
    /// passes this as <c>--scratch-root</c>).
    /// </summary>
    private static string CreateScratchDir(int apex, params string[] relativePaths)
    {
        var root = Path.Combine(Path.GetTempPath(), $"polyphony-archivist-{Guid.NewGuid():N}");
        var apexDir = Path.Combine(root, apex.ToString());
        Directory.CreateDirectory(apexDir);

        foreach (var rel in relativePaths)
        {
            var fullPath = Path.Combine(apexDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, $"# {rel}\nSample content for testing.");
        }

        return root;
    }

    private static void CleanupTempDir(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
