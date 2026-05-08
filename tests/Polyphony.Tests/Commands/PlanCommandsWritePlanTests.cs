using System.Text;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Models;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Phase 3 P7b foundation — unit tests for <see cref="PlanCommands.WritePlan"/>.
/// Covers happy path, no-op rewrite, content-json decoding errors, and
/// the input-validation error envelopes.
/// </summary>
public sealed class PlanCommandsWritePlanTests : CommandTestBase, IDisposable
{
    private readonly string _tempDir;

    public PlanCommandsWritePlanTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-write-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public new void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
        base.Dispose();
    }

    private PlanCommands CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        return new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner));
    }

    private static PlanWritePlanResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PlanWritePlanResult)!;

    private static string EncodeJson(string s) => JsonSerializer.Serialize(s);

    [Fact]
    public async Task ItemId_NonPositive_EmitsError()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(0, EncodeJson("# plan"), _tempDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("--item-id must be positive");
    }

    [Fact]
    public async Task ContentJson_Empty_EmitsError()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(123, "", _tempDir));
        exit.ShouldBe(ExitCodes.RoutingFailure);
        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("plan write-plan");
        envelope.MissingArgs.ShouldContain("--content-json");
    }

    [Fact]
    public async Task ContentJson_NotValidJson_EmitsError()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(123, "not json", _tempDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("not valid JSON");
    }

    [Fact]
    public async Task ContentJson_NotAString_EmitsError()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(123, "[1,2,3]", _tempDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("must decode to a JSON string");
    }

    [Fact]
    public async Task HappyPath_WritesFileWithCorrectName()
    {
        var cmd = CreateCommand();
        var content = "# Plan for item 4242\n\nSome markdown.";
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(4242, EncodeJson(content), _tempDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ItemId.ShouldBe(4242);
        result.Path.ShouldEndWith(Path.Combine(_tempDir, "plan-4242.md"));
        result.Unchanged.ShouldBeFalse();
        result.BytesWritten.ShouldBe(Encoding.UTF8.GetByteCount(content));

        File.Exists(result.Path).ShouldBeTrue();
        (await File.ReadAllTextAsync(result.Path)).ShouldBe(content);
    }

    [Fact]
    public async Task HappyPath_EmitsSha256OfContent()
    {
        var cmd = CreateCommand();
        var content = "# Plan\n";
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(1, EncodeJson(content), _tempDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        // Verify the hash matches what System.Security.Cryptography.SHA256 produces for the UTF-8 bytes:
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        result.ContentSha256.ShouldBe(expected);
        result.ContentSha256.Length.ShouldBe(64);
    }

    [Fact]
    public async Task ExistingFile_IdenticalContent_ReturnsUnchanged()
    {
        var cmd = CreateCommand();
        var content = "# Plan v1";
        // First write
        await CaptureConsoleAsync(() => cmd.WritePlan(7, EncodeJson(content), _tempDir));

        // Second write — identical content
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(7, EncodeJson(content), _tempDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Unchanged.ShouldBeTrue();
        result.BytesWritten.ShouldBe(Encoding.UTF8.GetByteCount(content));
    }

    [Fact]
    public async Task ExistingFile_DifferentContent_OverwritesAndUnchangedFalse()
    {
        var cmd = CreateCommand();
        await CaptureConsoleAsync(() => cmd.WritePlan(8, EncodeJson("v1"), _tempDir));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(8, EncodeJson("v2 — changed"), _tempDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Unchanged.ShouldBeFalse();
        (await File.ReadAllTextAsync(result.Path)).ShouldBe("v2 — changed");
    }

    [Fact]
    public async Task PlansDir_DoesNotExist_IsCreated()
    {
        var cmd = CreateCommand();
        var nestedDir = Path.Combine(_tempDir, "nested", "plans");
        Directory.Exists(nestedDir).ShouldBeFalse();

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(99, EncodeJson("plan"), nestedDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        Directory.Exists(nestedDir).ShouldBeTrue();
        File.Exists(result.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task EmptyPlanContent_StillWritesAndReportsZeroBytes()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(11, EncodeJson(""), _tempDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.BytesWritten.ShouldBe(0);
        File.Exists(result.Path).ShouldBeTrue();
        (await File.ReadAllTextAsync(result.Path)).ShouldBe("");
    }

    [Fact]
    public async Task UnicodeContent_RoundTripsCorrectly()
    {
        var cmd = CreateCommand();
        var content = "# 计划 — émoji 🎯 — \u4e2d\u6587\n";
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(42, EncodeJson(content), _tempDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        (await File.ReadAllTextAsync(result.Path)).ShouldBe(content);
        result.BytesWritten.ShouldBe(Encoding.UTF8.GetByteCount(content));
    }
}
