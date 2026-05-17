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
/// AB#3217 — round-trip tests for <c>polyphony branch mark-impl-merged</c>
/// and <c>polyphony branch clear-impl-merged</c>. The marker is the key
/// signal that lets <c>branch next-impl</c> skip an apex root whose
/// terminal transition is deferred to <c>close_mark_satisfied</c>
/// (AB#3169) so the same item doesn't redispatch and trigger empty-impl
/// squash-coverage failures.
/// </summary>
public sealed class BranchCommandsMarkImplMergedTests : CommandTestBase
{
    private (BranchCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var walker = new HierarchyWalker(Config, Repository);
        var validator = new TransitionValidator(Config);
        return (new BranchCommands(twig, walker, Repository, validator, git, Config,
            new Polyphony.Sdlc.Observers.RepoIdentityResolver(git),
            new Polyphony.Sdlc.Observers.PullRequestReader(gh, null)), runner);
    }

    private static void StubSync(FakeProcessRunner runner)
        => runner.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(0, "{}", ""));

    private static void StubConfig(FakeProcessRunner runner, string org = "org", string project = "proj")
    {
        runner.WhenExact("twig", ["config", "organization", "--output", "json"],
            new ProcessResult(0, $$"""{"info":"{{org}}"}""", ""));
        runner.WhenExact("twig", ["config", "project", "--output", "json"],
            new ProcessResult(0, $$"""{"info":"{{project}}"}""", ""));
    }

    /// <summary>
    /// Stub the <c>twig show</c> + <c>twig patch</c> boundary so the
    /// in-memory "tags after sync" value drives both the pre-check
    /// and the read-after-write assertion. Mirrors how the verb
    /// observes ADO state through twig — we never touch the
    /// cache repository directly because the verb doesn't either.
    /// </summary>
    private static void StubTagsRoundTrip(FakeProcessRunner runner, int workItemId, string initialTags)
    {
        // Single mutable holder so a subsequent `twig show` after `twig
        // patch` reflects the patched tags. Closures capture by reference.
        var state = new[] { initialTags };

        runner.WhenAsync(
            (e, a) => e == "twig"
                && a.Count >= 4
                && a[0] == "show"
                && a[1] == workItemId.ToString()
                && a[^1] == "json",
            (_, _) =>
            {
                var encoded = JsonEncodedText.Encode(state[0]).Value;
                var json = $$"""{"id":{{workItemId}},"tags":"{{encoded}}"}""";
                return Task.FromResult(new ProcessResult(0, json, ""));
            });

        runner.WhenAsync(
            (e, a) => e == "twig"
                && a.Count >= 5
                && a[0] == "patch"
                && a[1] == "--id"
                && a[2] == workItemId.ToString()
                && a[3] == "--json",
            (args, _) =>
            {
                // PatchFieldsAsync emits `patch --id <n> --json <fieldsJson>`.
                // Parse the JSON payload to extract the new System.Tags value
                // so subsequent `twig show` calls return the patched state.
                try
                {
                    using var doc = JsonDocument.Parse(args[4]);
                    if (doc.RootElement.TryGetProperty("System.Tags", out var tagsEl))
                    {
                        state[0] = tagsEl.GetString() ?? state[0];
                    }
                }
                catch (JsonException)
                {
                    // Test-only stub — fall through with state unchanged.
                }
                return Task.FromResult(new ProcessResult(0, "{}", ""));
            });
    }

    private static BranchImplMergedMarkerResult Deserialize(string output)
        => JsonSerializer.Deserialize(
                output,
                PolyphonyJsonContext.Default.BranchImplMergedMarkerResult)
            ?? throw new InvalidOperationException("Failed to deserialize impl-merged-marker output");

    [Fact]
    public async Task Mark_HappyPath_StampsTag_AlreadyInDesiredStateFalse()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root; PG-1");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MarkImplMerged(workItem: 100, mgPath: "pg-1"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Operation.ShouldBe("mark");
        result.WorkItemId.ShouldBe(100);
        result.MergeGroupKey.ShouldBe("pg-1");
        result.Tag.ShouldBe("polyphony:impl-merged-in-mg=pg-1");
        result.Success.ShouldBeTrue();
        result.AlreadyInDesiredState.ShouldBeFalse();
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Mark_TagAlreadyPresent_Idempotent_NoPatchInvoked()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root; polyphony:impl-merged-in-mg=pg-1");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MarkImplMerged(workItem: 100, mgPath: "pg-1"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Success.ShouldBeTrue();
        result.AlreadyInDesiredState.ShouldBeTrue();
        // No patch invocation — short-circuit avoids the round-trip.
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task Mark_PatchSucceedsButTagAbsentAfterSync_EmitsErrorWithAdoUrl()
    {
        // AB#3189 / AB#3191 mirror: simulate ADO eventual-consistency
        // race by overriding the show stub AFTER patch with a tag set
        // that doesn't include the marker.
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);

        // First show returns the pre-patch state; patch exits 0 but the
        // post-sync show STILL returns the pre-patch state (silent
        // failure on twig's side).
        runner.WhenExact("twig", ["show", "100", "--output", "json"],
            new ProcessResult(0, """{"id":100,"tags":"polyphony:root"}""", ""));
        runner.WhenStartsWith("twig", ["patch"], new ProcessResult(0, "{}", ""));

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.MarkImplMerged(workItem: 100, mgPath: "pg-1"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("#100");
        result.Error.ShouldContain("polyphony:impl-merged-in-mg=pg-1");
        result.Error.ShouldContain("https://dev.azure.com/org/proj/_workitems/edit/100");
    }

    [Fact]
    public async Task Clear_HappyPath_RemovesTag()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubTagsRoundTrip(runner, 100,
            "polyphony:root; polyphony:impl-merged-in-mg=pg-1; PG-1");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClearImplMerged(workItem: 100, mgPath: "pg-1"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Operation.ShouldBe("clear");
        result.Success.ShouldBeTrue();
        result.AlreadyInDesiredState.ShouldBeFalse();
    }

    [Fact]
    public async Task Clear_TagAlreadyAbsent_Idempotent_NoPatchInvoked()
    {
        var (cmd, runner) = CreateCommand();
        StubSync(runner);
        StubConfig(runner);
        StubTagsRoundTrip(runner, 100, "polyphony:root; PG-1");

        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ClearImplMerged(workItem: 100, mgPath: "pg-1"));

        exit.ShouldBe(ExitCodes.Success);
        var result = Deserialize(output);
        result.Success.ShouldBeTrue();
        result.AlreadyInDesiredState.ShouldBeTrue();
        runner.Invocations.ShouldNotContain(i =>
            i.Executable == "twig" && i.Arguments.Count > 0 && i.Arguments[0] == "patch");
    }

    [Fact]
    public async Task Mark_MissingWorkItem_Halts()
    {
        var (cmd, _) = CreateCommand();

        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.MarkImplMerged(mgPath: "pg-1"));

        // HaltIfMissing returns RoutingFailure for misconfigured invocations.
        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task Mark_MissingMgPath_Halts()
    {
        var (cmd, _) = CreateCommand();

        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.MarkImplMerged(workItem: 100));

        exit.ShouldBe(ExitCodes.RoutingFailure);
    }

    [Fact]
    public async Task Clear_MissingMgPath_Halts()
    {
        var (cmd, _) = CreateCommand();

        var (exit, _) = await CaptureConsoleAsync(
            () => cmd.ClearImplMerged(workItem: 100));

        exit.ShouldBe(ExitCodes.RoutingFailure);
    }
}
