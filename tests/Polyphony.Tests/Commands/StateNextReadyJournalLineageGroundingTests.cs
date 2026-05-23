using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal;
using Polyphony.Sdlc;
using Polyphony.Sdlc.Observers;
using Polyphony.Tagging;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.Stubs;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W8 (epic #3165): journal-grounded refusal of foreign
/// <c>polyphony:facets=*</c> tags in <see cref="StateCommands.NextReady"/>.
/// </summary>
/// <remarks>
/// The override-applied / no-tag / malformed-tag cases are covered by
/// <see cref="StateNextReadyRootFacetsTests"/>. This file pins the
/// W8-specific behaviour: when a real <see cref="JournalStore"/> is
/// wired AND the current lineage has no <c>plan_seed_children</c> row
/// for the item but another lineage does, the verb refuses to trust
/// the tag and falls back to the type-config default. Conversely, when
/// the current lineage DID record the seeding, the tag is trusted.
/// </remarks>
public sealed class StateNextReadyJournalLineageGroundingTests : CommandTestBase
{
    private const int RootId = 4801;
    private const string OriginUrl = "https://github.com/acme/repo.git";

    private readonly string _tempDir;
    private readonly JournalStore _store;

    public StateNextReadyJournalLineageGroundingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-state-w8-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
    }

    [Fact]
    public async Task NextReady_FacetsTag_StampedByForeignLineage_IsIgnored()
    {
        // Pre-seed the journal with a plan_seed_children row stamped by
        // a PRIOR run (run-old). The current lineage (run-current) has
        // no row for the item. W8 must refuse to honour the tag.
        await AppendRowAsync(runId: "run-old", workItemId: RootId, action: "plan_seed_children");

        var item = new WorkItemBuilder()
            .WithId(RootId).WithType("Issue").WithTitle("Root 4801").WithState("Doing")
            .WithTags($"polyphony;{PolyphonyTags.FacetsPrefix}={Facet.Plannable}")
            .Build();
        await SeedAsync(item);
        var runner = new FakeProcessRunner();
        BindBaseline(runner);

        var cmd = CreateCommand(runner, currentRunId: "run-current");
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: RootId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Status.ShouldNotBe("error");

        // With the foreign tag refused, the resolver falls back to the
        // type-config default — implementation_merged returns to the
        // requirement set (Issue type defaults to plannable+implementable).
        var kinds = result.Requirements.Select(r => r.Kind).ToHashSet();
        kinds.ShouldContain(RequirementKind.ImplementationMerged);
        result.ResolvedInputs.Facets.ShouldContain(Facet.Implementable);
    }

    [Fact]
    public async Task NextReady_FacetsTag_StampedByCurrentLineage_IsHonoured()
    {
        // Current lineage recorded the seeding — the tag is ours.
        await AppendRowAsync(runId: "run-current", workItemId: RootId, action: "plan_seed_children");

        var item = new WorkItemBuilder()
            .WithId(RootId).WithType("Issue").WithTitle("Root 4801").WithState("Doing")
            .WithTags($"polyphony;{PolyphonyTags.FacetsPrefix}={Facet.Plannable}")
            .Build();
        await SeedAsync(item);
        var runner = new FakeProcessRunner();
        BindBaseline(runner);

        var cmd = CreateCommand(runner, currentRunId: "run-current");
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: RootId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Status.ShouldNotBe("error");

        // Tag honoured → narrowed to plannable only, implementation_merged
        // drops out (same expectation as StateNextReadyRootFacetsTests'
        // happy-path assertion).
        var kinds = result.Requirements.Select(r => r.Kind).ToHashSet();
        kinds.ShouldNotContain(RequirementKind.ImplementationMerged);
        result.ResolvedInputs.Facets.ShouldBe([Facet.Plannable]);
    }

    [Fact]
    public async Task NextReady_FacetsTag_ManualLineage_TagAlwaysTrusted()
    {
        // Same prior-lineage row as the refuse-case, but the current
        // RunContext is a manual_* fallback — we can't ground anything,
        // so the tag must be trusted (status quo).
        await AppendRowAsync(runId: "run-old", workItemId: RootId, action: "plan_seed_children");

        var item = new WorkItemBuilder()
            .WithId(RootId).WithType("Issue").WithTitle("Root 4801").WithState("Doing")
            .WithTags($"polyphony;{PolyphonyTags.FacetsPrefix}={Facet.Plannable}")
            .Build();
        await SeedAsync(item);
        var runner = new FakeProcessRunner();
        BindBaseline(runner);

        var cmd = CreateCommand(runner, currentRunId: "manual_no_env");
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: RootId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        var kinds = result.Requirements.Select(r => r.Kind).ToHashSet();
        kinds.ShouldNotContain(RequirementKind.ImplementationMerged);
    }

    private StateCommands CreateCommand(FakeProcessRunner runner, string currentRunId)
    {
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var planObserver = new PlanObserver(git, gh, new ThrowingAdoClient(), twig, new RepoIdentityResolver(git));
        return new StateCommands(twig, git, gh, runner, Repository, Config, planObserver,
            JournalTestSupport.CreateRunContext(currentRunId), _store);
    }

    private static void BindBaseline(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, OriginUrl + "\n", ""));
        runner.WhenStartsWith("git", ["ls-remote"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));
        runner.WhenStartsWith("twig", ["show"], new ProcessResult(0,
            $$"""{"id":{{RootId}},"title":"Root","tags":""}""", ""));
    }

    private async Task AppendRowAsync(string runId, int workItemId, string action)
    {
        var id = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = runId,
                RootId = workItemId,
                WorkItemId = workItemId,
                Action = action,
                Target = $"workitem:{workItemId}",
                StartedAt = 1_700_000_000_000,
            },
            CancellationToken.None);
        await _store.RecordEndAsync(id, JournalOutcome.Success, null, null, null, null, CancellationToken.None);
    }

    public override void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }
}
