using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Polyphony;
using Polyphony.Journal;
using Polyphony.Tests.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal;

/// <summary>
/// W5 (AB#3279): the fail-closed mutation guard on JournaledActionDecorator
/// refuses to invoke any mutating action when the run id falls back to the
/// `manual_*` synthetic shape (POLYPHONY_RUN_ID was unset). The guard
/// writes ZERO journal rows so the refusal doesn't pollute whichever real
/// lineage the launcher would later stamp.
/// </summary>
public sealed class JournaledActionDecoratorManualLineageGuardTests
{
    private const string SampleManualRunId = "manual_abcdef0123456789abcdef0123456789";
    private const string SampleUlidRunId = "01HZK7Y9ABCDEF0123456789AB";

    [Fact]
    public async Task RunWithAsync_GuardEngaged_ManualLineage_RefusesAndReturnsExitCode5()
    {
        var store = new RecordingJournalStore();
        var decorator = new JournaledActionDecorator(store, failClosedOnManualLineage: true);
        var actionFired = false;

        int exitCode;
        var stderrCapture = new StringWriter();
        await ConsoleTestLock.AsyncLock.WaitAsync();
        var priorErr = Console.Error;
        Console.SetError(stderrCapture);
        try
        {
            exitCode = await decorator.RunWithAsync(
                new JournaledActionInvocation
                {
                    RunId = SampleManualRunId,
                    RootId = 1234,
                    WorkItemId = 5678,
                    Action = "branch.ensure_plan",
                    Target = "plan/1234",
                },
                _ =>
                {
                    actionFired = true;
                    return Task.FromResult(ExitCodes.Success);
                });
        }
        finally
        {
            Console.SetError(priorErr);
            ConsoleTestLock.AsyncLock.Release();
        }

        exitCode.ShouldBe(ExitCodes.MissingRunIdLineage);
        ExitCodes.MissingRunIdLineage.ShouldBe(5);
        actionFired.ShouldBeFalse();
        store.Starts.Count.ShouldBe(0);
        store.Ends.Count.ShouldBe(0);

        var stderr = stderrCapture.ToString().Trim();
        stderr.ShouldNotBeEmpty();
        var envelope = JsonSerializer.Deserialize<ManualLineageRefusal>(
            stderr, PolyphonyJsonContext.Default.ManualLineageRefusal);
        envelope.ShouldNotBeNull();
        envelope!.Error.ShouldBe("missing_run_id_lineage");
        envelope.Verb.ShouldBe("branch.ensure_plan");
        envelope.Target.ShouldBe("plan/1234");
        envelope.EnvVar.ShouldBe("POLYPHONY_RUN_ID");
        envelope.Message.ShouldContain("POLYPHONY_RUN_ID");
        envelope.Message.ShouldContain("Invoke-PolyphonySdlc.ps1");
    }

    [Fact]
    public async Task RunWithAsync_GuardEngaged_RealUlidRunId_AllowsActionAndJournals()
    {
        var store = new RecordingJournalStore();
        var decorator = new JournaledActionDecorator(store, failClosedOnManualLineage: true);

        var exitCode = await decorator.RunWithAsync(
            new JournaledActionInvocation
            {
                RunId = SampleUlidRunId,
                RootId = 1234,
                Action = "branch.ensure_plan",
                Target = "plan/1234",
            },
            _ => Task.FromResult(ExitCodes.Success));

        exitCode.ShouldBe(ExitCodes.Success);
        store.Starts.Count.ShouldBe(1);
        store.Ends.Count.ShouldBe(1);
        store.Starts[0].RunId.ShouldBe(SampleUlidRunId);
        store.Ends[0].Outcome.ShouldBe(JournalOutcome.Success);
    }

    [Fact]
    public async Task RunWithAsync_DefaultCtor_ManualLineage_DoesNotRefuse()
    {
        // The default ctor (used by 7 existing test fixtures) leaves the
        // guard disengaged so tests that construct decorators without an
        // env-stamped run id continue to work.
        var store = new RecordingJournalStore();
        var decorator = new JournaledActionDecorator(store);

        decorator.WithManualLineageGuard.ShouldBeFalse();

        var exitCode = await decorator.RunWithAsync(
            new JournaledActionInvocation
            {
                RunId = SampleManualRunId,
                Action = "branch.ensure_plan",
                Target = "plan/1234",
            },
            _ => Task.FromResult(ExitCodes.Success));

        exitCode.ShouldBe(ExitCodes.Success);
        store.Starts.Count.ShouldBe(1);
    }

    [Fact]
    public void Constructor_GuardFlag_ExposedViaWithManualLineageGuardProperty()
    {
        var store = new RecordingJournalStore();
        new JournaledActionDecorator(store, failClosedOnManualLineage: true).WithManualLineageGuard.ShouldBeTrue();
        new JournaledActionDecorator(store, failClosedOnManualLineage: false).WithManualLineageGuard.ShouldBeFalse();
        new JournaledActionDecorator(store).WithManualLineageGuard.ShouldBeFalse();
    }

    [Fact]
    public async Task RunWithAsync_GuardEngaged_ManualLineage_DoesNotWriteAnyJournalRow()
    {
        // Belt-and-suspenders: the refusal contract is that the launcher
        // can re-run the same invocation under a real lineage and the
        // journal must look pristine — no orphan start row, no failure
        // row from the refused attempt.
        var store = new RecordingJournalStore();
        var decorator = new JournaledActionDecorator(store, failClosedOnManualLineage: true);

        var priorErr = Console.Error;
        await ConsoleTestLock.AsyncLock.WaitAsync();
        Console.SetError(TextWriter.Null);
        try
        {
            await decorator.RunWithAsync(
                new JournaledActionInvocation
                {
                    RunId = SampleManualRunId,
                    Action = "branch.ensure_plan",
                    Target = "plan/1234",
                },
                _ => Task.FromResult(ExitCodes.Success));
        }
        finally
        {
            Console.SetError(priorErr);
            ConsoleTestLock.AsyncLock.Release();
        }

        store.Starts.Count.ShouldBe(0);
        store.Ends.Count.ShouldBe(0);

        // Second invocation under real lineage now journals as normal.
        await decorator.RunWithAsync(
            new JournaledActionInvocation
            {
                RunId = SampleUlidRunId,
                Action = "branch.ensure_plan",
                Target = "plan/1234",
            },
            _ => Task.FromResult(ExitCodes.Success));

        store.Starts.Count.ShouldBe(1);
        store.Starts[0].RunId.ShouldBe(SampleUlidRunId);
    }

    private sealed class RecordingJournalStore : IJournalStore
    {
        public List<JournalEntryStart> Starts { get; } = new();
        public List<(long ActionId, JournalOutcome Outcome)> Ends { get; } = new();
        public string DatabasePath => ":memory:";

        public Task<long> RecordStartAsync(JournalEntryStart entry, CancellationToken ct)
        {
            Starts.Add(entry);
            return Task.FromResult((long)Starts.Count);
        }

        public Task RecordEndAsync(
            long actionId,
            JournalOutcome outcome,
            string? errorCode,
            string? errorMessage,
            string? payloadJson,
            IReadOnlyList<JournalResourceEffect>? effects,
            CancellationToken ct)
        {
            Ends.Add((actionId, outcome));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<JournalEntry>> QueryAsync(JournalQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JournalEntry>>(Array.Empty<JournalEntry>());

        public Task ExportAsync(string destinationPath, CancellationToken ct) => Task.CompletedTask;

        public Task RecordLineageAsync(string runId, int rootId, string? host, string? user, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<JournalLineage>> GetLineagesAsync(int rootId, CancellationToken ct) => Task.FromResult<IReadOnlyList<JournalLineage>>([]);
        public Task<bool> RetireLineageAsync(string runId, int rootId, string? reason, CancellationToken ct) => Task.FromResult(false);
    }
}
