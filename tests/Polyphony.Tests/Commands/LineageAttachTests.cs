using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Journal;
using Polyphony.Models;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W14 (AB#3295): <c>polyphony lineage attach</c> — cross-machine
/// on-ramp. Verifies dry-run vs execute, idempotent re-attach,
/// retirement refusal, and the NullJournalStore short-circuit.
/// </summary>
public sealed class LineageAttachTests : CommandTestBase
{
    private readonly string _tempDir;
    private readonly JournalStore _store;

    public LineageAttachTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-lineage-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
    }

    [Fact]
    public async Task Attach_RootMissing_ReturnsRequiredInputHalt()
    {
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(() => command.Attach(runId: "01JCYYYYYYYYYYYYYYYYYYYYYY"));

        exitCode.ShouldNotBe(ExitCodes.Success);
        output.ShouldContain("--root");
    }

    [Fact]
    public async Task Attach_RunIdMissing_ReturnsRequiredInputHalt()
    {
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(() => command.Attach(root: 7700));

        exitCode.ShouldNotBe(ExitCodes.Success);
        output.ShouldContain("--run-id");
    }

    [Fact]
    public async Task Attach_DryRun_DoesNotWrite()
    {
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(() =>
            command.Attach(root: 7701, runId: "01JCZZZZZZZZZZZZZZZZZZZZZZ", reason: "fresh checkout"));
        var result = Deserialize(output);

        exitCode.ShouldBe(ExitCodes.Success);
        result.Success.ShouldBeTrue();
        result.Executed.ShouldBeFalse();
        result.AlreadyExisted.ShouldBeFalse();
        result.Reason.ShouldBe("fresh checkout");

        var lineages = await _store.GetLineagesAsync(7701, CancellationToken.None);
        lineages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Attach_Execute_WritesStub()
    {
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(() =>
            command.Attach(root: 7702, runId: "01JCAAAAAAAAAAAAAAAAAAAAAA", execute: true));
        var result = Deserialize(output);

        exitCode.ShouldBe(ExitCodes.Success);
        result.Executed.ShouldBeTrue();
        result.AlreadyExisted.ShouldBeFalse();

        var lineages = await _store.GetLineagesAsync(7702, CancellationToken.None);
        lineages.ShouldHaveSingleItem();
        lineages[0].RunId.ShouldBe("01JCAAAAAAAAAAAAAAAAAAAAAA");
        lineages[0].RetiredAt.ShouldBeNull();
    }

    [Fact]
    public async Task Attach_AlreadyExists_Idempotent()
    {
        await _store.RecordLineageAsync("01JCBBBBBBBBBBBBBBBBBBBBBB", 7703, "host", "user", CancellationToken.None);

        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(() =>
            command.Attach(root: 7703, runId: "01JCBBBBBBBBBBBBBBBBBBBBBB", execute: true));
        var result = Deserialize(output);

        exitCode.ShouldBe(ExitCodes.Success);
        result.AlreadyExisted.ShouldBeTrue();

        var lineages = await _store.GetLineagesAsync(7703, CancellationToken.None);
        lineages.ShouldHaveSingleItem(); // no duplicate row
    }

    [Fact]
    public async Task Attach_Retired_RefusesWithRoutingFailure()
    {
        await _store.RecordLineageAsync("01JCCCCCCCCCCCCCCCCCCCCCCC", 7704, "host", "user", CancellationToken.None);
        await _store.RetireLineageAsync("01JCCCCCCCCCCCCCCCCCCCCCCC", 7704, "test retire", CancellationToken.None);

        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(() =>
            command.Attach(root: 7704, runId: "01JCCCCCCCCCCCCCCCCCCCCCCC", execute: true));
        var result = Deserialize(output);

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
        result.Success.ShouldBeFalse();
        result.AlreadyExisted.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("retired");
    }

    [Fact]
    public async Task Attach_NullJournalStore_ShortCircuitsSuccess()
    {
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), new NullJournalStore());
        var (exitCode, output) = await CaptureConsoleAsync(() =>
            command.Attach(root: 7705, runId: "01JCDDDDDDDDDDDDDDDDDDDDDD", execute: true));
        var result = Deserialize(output);

        exitCode.ShouldBe(ExitCodes.Success);
        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Attach_NegativeRoot_ReturnsConfigError()
    {
        var command = new LineageCommands(JournalTestSupport.CreateRunContext("manual_test"), _store);
        var (exitCode, output) = await CaptureConsoleAsync(() =>
            command.Attach(root: -1, runId: "01JCEEEEEEEEEEEEEEEEEEEEEE"));
        var result = Deserialize(output);

        exitCode.ShouldBe(ExitCodes.ConfigError);
        result.Success.ShouldBeFalse();
    }

    private static LineageAttachResult Deserialize(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.LineageAttachResult)!;
}
