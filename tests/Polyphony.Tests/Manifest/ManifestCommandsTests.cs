using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Manifest;
using Polyphony.Tests.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// End-to-end tests for the <c>polyphony manifest</c> verbs. The verbs
/// take no constructor dependencies and operate on a path passed via
/// <c>--path</c>, so each test uses a temp file to stay hermetic.
/// </summary>
public sealed class ManifestCommandsTests : IDisposable
{
    private readonly string tempDir;
    private readonly string manifestPath;

    public ManifestCommandsTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "polyphony-manifest-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);
        this.manifestPath = Path.Combine(this.tempDir, "run.yaml");
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static ManifestCommands NewCommand() => new();

    /// <summary>
    /// Runs an async command while capturing stdout. Mirrors
    /// <c>CommandTestBase.CaptureConsoleAsync</c>; we can't inherit
    /// because we don't need the SQLite/DI scaffolding.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> CaptureAsync(Func<Task<int>> action)
    {
        await ConsoleTestLock.AsyncLock.WaitAsync();
        try
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var exit = await action();
                return (exit, writer.ToString().Trim());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }
    }

    [Fact]
    public async Task Init_FreshPath_CreatesManifestAndEmitsResult()
    {
        var cmd = NewCommand();
        var (exit, output) = await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "dev.azure.com/org/project", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        File.Exists(this.manifestPath).ShouldBeTrue();

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestInitResult)!;
        result.RootId.ShouldBe(1234);
        result.PlatformProject.ShouldBe("dev.azure.com/org/project");
        result.Created.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.TopologyHash.ShouldStartWith("sha256:");
    }

    [Fact]
    public async Task Init_NonPositiveRootId_ReturnsConfigError()
    {
        var cmd = NewCommand();
        var (exit, output) = await CaptureAsync(() =>
            cmd.Init(rootId: 0, platformProject: "dev.azure.com/org/project", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        File.Exists(this.manifestPath).ShouldBeFalse();

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestInitResult)!;
        result.Error!.ShouldContain("rootId");
    }

    [Fact]
    public async Task Init_EmptyPlatformProject_ReturnsConfigError()
    {
        var cmd = NewCommand();
        var (exit, output) = await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "  ", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestInitResult)!;
        result.Error!.ShouldContain("platform-project");
    }

    [Fact]
    public async Task Init_AlreadyExists_WithoutForce_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.Init(rootId: 5678, platformProject: "x/y", path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestInitResult)!;
        result.Error!.ShouldContain("already exists");
    }

    [Fact]
    public async Task Init_AlreadyExists_WithForce_OverwritesAndReportsCreatedFalse()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() =>
            cmd.Init(rootId: 5678, platformProject: "x/y", path: this.manifestPath, force: true));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestInitResult)!;
        result.RootId.ShouldBe(5678);
        result.Created.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
    }

    [Fact]
    public async Task Read_HappyPath_RoundTripsAndReportsHashMatch()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.Read(path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadResult)!;
        result.Manifest.RootId.ShouldBe(1234);
        result.TopologyHashMatches.ShouldBeTrue();
        result.ComputedTopologyHash.ShouldBe(result.Manifest.TopologyHash);
    }

    [Fact]
    public async Task Read_MissingFile_ReturnsCacheError()
    {
        var cmd = NewCommand();
        var (exit, output) = await CaptureAsync(() =>
            cmd.Read(path: Path.Combine(this.tempDir, "missing.yaml")));

        exit.ShouldBe(ExitCodes.CacheError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadResult)!;
        result.Error!.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Read_MalformedYaml_ReturnsConfigError()
    {
        File.WriteAllText(this.manifestPath, "schema: 1\nroot_id: not-a-number\n");

        var cmd = NewCommand();
        var (exit, output) = await CaptureAsync(() => cmd.Read(path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestReadResult)!;
        result.Error!.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TopologyHash_FreshManifest_MatchesStored()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.TopologyHash(path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestTopologyHashResult)!;
        result.Matches.ShouldBeTrue();
        result.TopologyHash.ShouldBe(result.StoredTopologyHash);
    }

    [Fact]
    public async Task TopologyHash_ManuallyMutatedManifest_ReportsMismatch()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        // Replace the stored topology_hash line with a known-bad value.
        var lines = File.ReadAllLines(this.manifestPath)
            .Select(line => line.StartsWith("topology_hash:", StringComparison.Ordinal)
                ? "topology_hash: sha256:0000000000000000000000000000000000000000000000000000000000000000"
                : line);
        File.WriteAllLines(this.manifestPath, lines);

        var (exit, output) = await CaptureAsync(() => cmd.TopologyHash(path: this.manifestPath));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestTopologyHashResult)!;
        result.Matches.ShouldBeFalse();
        result.TopologyHash.ShouldNotBe(result.StoredTopologyHash);
    }

    [Fact]
    public async Task RecordRebase_HappyPath_AppendsAndReportsCount()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.RecordRebase(
            branch: "mg/1234_data-layer",
            onto: "feature/1234",
            reason: "cross_mg_code_dep",
            commit: "0b1f3e9",
            path: this.manifestPath,
            at: "2026-05-06T18:00:00Z"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRebaseRecordResult)!;
        result.RebaseCount.ShouldBe(1);
        result.Branch.ShouldBe("mg/1234_data-layer");

        // Persistence check: the next call should report count 2.
        var (_, output2) = await CaptureAsync(() => cmd.RecordRebase(
            branch: "mg/1234_other",
            onto: "feature/1234",
            reason: "manual",
            commit: "abc1234",
            path: this.manifestPath));
        var result2 = JsonSerializer.Deserialize(output2, PolyphonyJsonContext.Default.ManifestRebaseRecordResult)!;
        result2.RebaseCount.ShouldBe(2);
    }

    [Fact]
    public async Task RecordRebase_MissingField_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.RecordRebase(
            branch: "",
            onto: "feature/1234",
            reason: "manual",
            commit: "abc",
            path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRebaseRecordResult)!;
        result.Error!.ShouldContain("non-empty");
    }

    [Fact]
    public async Task RecordRebase_BadTimestamp_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.RecordRebase(
            branch: "mg/1234_data",
            onto: "feature/1234",
            reason: "manual",
            commit: "abc",
            path: this.manifestPath,
            at: "not-a-timestamp"));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestRebaseRecordResult)!;
        result.Error!.ShouldContain("ISO-8601");
    }

    [Fact]
    public async Task RecordApproval_HappyPath_AppendsAndReportsCount()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.RecordApproval(
            gate: "deep_nesting_depth_4",
            approvedBy: "dangreen",
            path: this.manifestPath,
            detail: "approved at depth 4",
            at: "2026-05-06T17:00:00Z"));

        exit.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestApprovalRecordResult)!;
        result.ApprovalCount.ShouldBe(1);
        result.Gate.ShouldBe("deep_nesting_depth_4");
        result.Detail.ShouldBe("approved at depth 4");
    }

    [Fact]
    public async Task RecordApproval_MissingField_ReturnsConfigError()
    {
        var cmd = NewCommand();
        await CaptureAsync(() =>
            cmd.Init(rootId: 1234, platformProject: "x/y", path: this.manifestPath));

        var (exit, output) = await CaptureAsync(() => cmd.RecordApproval(
            gate: "",
            approvedBy: "dangreen",
            path: this.manifestPath));

        exit.ShouldBe(ExitCodes.ConfigError);
        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.ManifestApprovalRecordResult)!;
        result.Error!.ShouldContain("non-empty");
    }
}
