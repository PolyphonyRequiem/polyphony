using System.Text;
using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Models;
using Polyphony.Routing;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
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
        return new PlanCommands(walker, Repository, Config, twig, new GitClient(runner), new GhClient(runner), new ThrowingAdoClient(), new FakePostconditionVerifier(), new Polyphony.Infrastructure.Paths.PolyphonyStatePaths(new GitClient(runner)), new Polyphony.Sdlc.Observers.RepoIdentityResolver(new GitClient(runner)), new Polyphony.Sdlc.Observers.PullRequestReader(new GhClient(runner), null));
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

    // ---------------------------------------------------------------------
    // Children-json sidecar tests (AB#3106 dogfood, 2026-05-12).
    //
    // The sidecar is the durable handoff between the architect (which emits
    // the children list in workflow-runtime context only) and the seeder
    // (which may run in a later workflow execution where that runtime
    // context has been reset). These tests pin down the verb's contract:
    // arg-omitted is a no-op; arg-supplied always writes (even `[]`);
    // refusal-before-write atomicity guarantees no half-applied state.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ChildrenJson_Omitted_DoesNotEmitSidecar()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(123, EncodeJson("# plan"), _tempDir));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ChildrenSkipped.ShouldBeTrue();
        result.ChildrenPath.ShouldBe(string.Empty);
        result.ChildrenSha256.ShouldBe(string.Empty);
        result.ChildrenUnchanged.ShouldBeFalse();
        File.Exists(Path.Combine(_tempDir, "plan-123.children.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task ChildrenJson_EmptyArray_WritesSidecarAndReportsHash()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(123, EncodeJson("# plan"), _tempDir, "[]"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ChildrenSkipped.ShouldBeFalse();
        result.ChildrenPath.ShouldEndWith(Path.Combine(_tempDir, "plan-123.children.json"));
        result.ChildrenSha256.Length.ShouldBe(64);
        result.ChildrenUnchanged.ShouldBeFalse();
        File.Exists(result.ChildrenPath).ShouldBeTrue();
        // Canonicalized indented form of an empty array is "[]" (no newline,
        // no whitespace — JsonNode's WriteIndented makes empty arrays compact).
        (await File.ReadAllTextAsync(result.ChildrenPath)).ShouldBe("[]");
    }

    [Fact]
    public async Task ChildrenJson_ValidArray_WritesCanonicalSidecar()
    {
        var cmd = CreateCommand();
        var children = "[{\"child_id\":\"c1\",\"title\":\"first\",\"type\":\"Issue\"}]";
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(456, EncodeJson("# plan"), _tempDir, children));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ChildrenSkipped.ShouldBeFalse();
        File.Exists(result.ChildrenPath).ShouldBeTrue();

        // Sidecar must round-trip as a JSON array (canonicalized via the
        // verb's indented-writer; we don't pin exact indentation here but
        // we DO require it parses back to the same logical content).
        var sidecarText = await File.ReadAllTextAsync(result.ChildrenPath);
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(sidecarText);
        parsed.ShouldBeOfType<System.Text.Json.Nodes.JsonArray>();
        var arr = (System.Text.Json.Nodes.JsonArray)parsed;
        arr.Count.ShouldBe(1);
        arr[0]!["child_id"]!.GetValue<string>().ShouldBe("c1");
        arr[0]!["title"]!.GetValue<string>().ShouldBe("first");
        arr[0]!["type"]!.GetValue<string>().ShouldBe("Issue");
    }

    [Fact]
    public async Task ChildrenJson_NonArray_ErrorsAndWritesNeitherFile()
    {
        var cmd = CreateCommand();
        // Pre-validation must catch this BEFORE the markdown is written —
        // otherwise we'd leave a half-applied state (md updated, sidecar
        // missing) that breaks the commit_and_push --paths contract.
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(789, EncodeJson("# plan"), _tempDir, "{\"not\":\"array\"}"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("--children-json must decode to a JSON array");
        File.Exists(Path.Combine(_tempDir, "plan-789.md")).ShouldBeFalse();
        File.Exists(Path.Combine(_tempDir, "plan-789.children.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task ChildrenJson_Malformed_ErrorsAndWritesNeitherFile()
    {
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(790, EncodeJson("# plan"), _tempDir, "[not valid json"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("--children-json is not valid JSON");
        File.Exists(Path.Combine(_tempDir, "plan-790.md")).ShouldBeFalse();
        File.Exists(Path.Combine(_tempDir, "plan-790.children.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task ChildrenJson_ExplicitNull_RejectedAsNonArray()
    {
        // Rubber-duck #3 (AB#3106 dogfood, 2026-05-12): JsonNode.Parse("null")
        // returns null and the previous null-tolerant guard would have silently
        // treated this as "skipped". Reject explicitly so a malformed
        // architect output can't slip through.
        var cmd = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(791, EncodeJson("# plan"), _tempDir, "null"));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("got null");
        File.Exists(Path.Combine(_tempDir, "plan-791.md")).ShouldBeFalse();
        File.Exists(Path.Combine(_tempDir, "plan-791.children.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task ChildrenJson_IdenticalRewrite_ReportsUnchanged()
    {
        var cmd = CreateCommand();
        var children = "[{\"child_id\":\"c1\",\"title\":\"x\",\"type\":\"Issue\"}]";
        await CaptureConsoleAsync(() =>
            cmd.WritePlan(901, EncodeJson("# plan"), _tempDir, children));

        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(901, EncodeJson("# plan"), _tempDir, children));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.Unchanged.ShouldBeTrue();
        result.ChildrenUnchanged.ShouldBeTrue();
        result.ChildrenSkipped.ShouldBeFalse();
    }

    [Fact]
    public async Task ChildrenJson_DifferentRewrite_ChildrenUnchangedFalse()
    {
        var cmd = CreateCommand();
        await CaptureConsoleAsync(() =>
            cmd.WritePlan(902, EncodeJson("# plan"), _tempDir, "[]"));

        var nextChildren = "[{\"child_id\":\"c1\",\"title\":\"x\",\"type\":\"Issue\"}]";
        var (exit, output) = await CaptureConsoleAsync(() =>
            cmd.WritePlan(902, EncodeJson("# plan"), _tempDir, nextChildren));
        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Error.ShouldBeNull();
        result.ChildrenUnchanged.ShouldBeFalse();
        var sidecarText = await File.ReadAllTextAsync(result.ChildrenPath);
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(sidecarText)!;
        parsed.AsArray().Count.ShouldBe(1);
    }
}
