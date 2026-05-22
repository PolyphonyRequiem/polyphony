using System.Reflection;
using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal;

public sealed class JournaledActionDecoratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JournalStore _store;
    private readonly DummyJournaledVerb _verb;

    public JournaledActionDecoratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-journal-decorator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
        _verb = new DummyJournaledVerb(new JournaledActionDecorator(_store));
    }

    [Fact]
    public async Task RunWithAsync_OnSuccess_RecordsStartAndEnd()
    {
        var exitCode = await _verb.RunSuccessAsync();
        var entries = await _store.QueryAsync(new JournalQuery { Action = "dummy_success" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.Success);
        entries.Count.ShouldBe(1);
        entries[0].Outcome.ShouldBe(JournalOutcome.Success);
        entries[0].FinishedAt.ShouldNotBeNull();
        entries[0].Target.ShouldBe("feature/3260");
        entries[0].PayloadJson.ShouldBe("{\"result\":\"success\"}");

        var attribute = typeof(DummyJournaledVerb).GetMethod(nameof(DummyJournaledVerb.RunSuccessAsync))!
            .GetCustomAttribute<JournaledActionAttribute>();
        attribute.ShouldNotBeNull();
        attribute!.Action.ShouldBe("dummy_success");
    }

    [Fact]
    public async Task RunWithAsync_OnFailureExitCode_RecordsFailure()
    {
        var exitCode = await _verb.RunFailureAsync();
        var entries = await _store.QueryAsync(new JournalQuery { Action = "dummy_failure" }, CancellationToken.None);

        exitCode.ShouldBe(ExitCodes.RoutingFailure);
        entries.Count.ShouldBe(1);
        entries[0].Outcome.ShouldBe(JournalOutcome.Failure);
        entries[0].ErrorCode.ShouldBe("exit_code_1");
        entries[0].ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task RunWithAsync_OnException_RecordsFailureAndRethrows()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _verb.RunThrowsAsync());
        var entries = await _store.QueryAsync(new JournalQuery { Action = "dummy_exception" }, CancellationToken.None);

        ex.Message.ShouldBe("boom");
        entries.Count.ShouldBe(1);
        entries[0].Outcome.ShouldBe(JournalOutcome.Failure);
        entries[0].ErrorCode.ShouldBe(nameof(InvalidOperationException));
        entries[0].ErrorMessage.ShouldBe("boom");
        entries[0].FinishedAt.ShouldNotBeNull();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class DummyJournaledVerb(JournaledActionDecorator decorator)
    {
        [JournaledAction(Action = "dummy_success")]
        public Task<int> RunSuccessAsync(CancellationToken ct = default)
        {
            return decorator.RunWithAsync(
                new JournaledActionInvocation
                {
                    RunId = "run-success",
                    RootId = 3260,
                    WorkItemId = 3260,
                    Action = "dummy_success",
                    Target = "feature/3260",
                },
                _ => Task.FromResult(ExitCodes.Success),
                payloadSelector: _ => "{\"result\":\"success\"}",
                ct: ct);
        }

        [JournaledAction(Action = "dummy_failure")]
        public Task<int> RunFailureAsync(CancellationToken ct = default)
        {
            return decorator.RunWithAsync(
                new JournaledActionInvocation
                {
                    RunId = "run-failure",
                    RootId = 3260,
                    WorkItemId = 3261,
                    Action = "dummy_failure",
                    Target = "feature/3261",
                },
                _ => Task.FromResult(ExitCodes.RoutingFailure),
                ct: ct);
        }

        [JournaledAction(Action = "dummy_exception")]
        public Task<int> RunThrowsAsync(CancellationToken ct = default)
        {
            return decorator.RunWithAsync(
                new JournaledActionInvocation
                {
                    RunId = "run-exception",
                    RootId = 3260,
                    WorkItemId = 3262,
                    Action = "dummy_exception",
                    Target = "feature/3262",
                },
                _ => throw new InvalidOperationException("boom"),
                ct: ct);
        }
    }
}
