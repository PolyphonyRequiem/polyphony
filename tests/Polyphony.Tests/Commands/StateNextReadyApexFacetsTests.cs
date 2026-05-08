using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;
using Polyphony.Sdlc;
using Polyphony.Sdlc.Observers;
using Polyphony.Tagging;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Integration tests for closed-loop PR #5's second leg: threading the
/// <c>polyphony:facets=&lt;csv&gt;</c> tag (PR #7) through
/// <see cref="StateCommands.NextReady"/> via
/// <see cref="RequirementInputResolver"/>. Architects stamp this tag on
/// an apex when they choose NOT to decompose; the resolver then derives
/// the per-item requirement set against the declared facet subset
/// instead of the type-config default.
/// </summary>
/// <remarks>
/// <para>
/// Two postures are validated end-to-end through the verb (not in
/// isolation against the resolver): the override case (tag present →
/// derived set excludes kinds tied to the omitted facets) and the
/// malformed-tag case (unknown facet → routing-style error envelope on
/// stdout with <see cref="ExitCodes.ConfigError"/>). The baseline (no
/// tag → fall back to type-config default) is exercised throughout the
/// existing PR #2/#3/#4 suites; one explicit pair-test below pins the
/// regression in case the override path ever leaks into the no-tag
/// branch.
/// </para>
/// <para>
/// Mocks the same <c>git remote get-url</c>, <c>git ls-remote</c>,
/// <c>gh pr list</c>, <c>twig show</c> shell-outs as the rest of the
/// next-ready integration suites; values are tuned so every observable
/// disposition would naturally land at Needed regardless of which
/// facets are in scope, leaving the requirement-set membership as the
/// only signal that varies between the override and no-override cases.
/// </para>
/// </remarks>
public sealed class StateNextReadyApexFacetsTests : CommandTestBase
{
    private const int ApexId = 4001;
    private const string OriginUrl = "https://github.com/acme/repo.git";

    private StateCommands CreateCommand(FakeProcessRunner runner)
    {
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        var planObserver = new PlanObserver(git, gh, twig);
        return new StateCommands(twig, git, gh, runner, Repository, Config, planObserver);
    }

    /// <summary>Register the "no PR / no signal" baseline shell-outs so
    /// every observable disposition lands at Needed regardless of the
    /// resolved facet set. Tests then only assert on requirement-set
    /// membership / resolved-inputs provenance, which is the actual
    /// signal under test.</summary>
    private static void BindBaseline(FakeProcessRunner runner)
    {
        runner.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, OriginUrl + "\n", ""));
        runner.WhenStartsWith("git", ["ls-remote"], new ProcessResult(0, "", ""));
        runner.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));
        runner.WhenStartsWith("twig", ["show"], new ProcessResult(0,
            $$"""{"id":{{ApexId}},"title":"Apex","tags":""}""", ""));
    }

    // ─── Override applied → derived set narrows to declared facets ─────

    [Fact]
    public async Task NextReady_TaggedFacetsOverride_NarrowsRequirementSetToDeclaredFacets()
    {
        // Issue type defaults to facets [plannable, implementable] in
        // CommandTestBase.Config — without an override the requirement
        // set carries plan_authored/reviewed/promoted, children_seeded,
        // implementation_merged, item_satisfied. Stamping
        // polyphony:facets=plannable on the item must drop
        // implementation_merged from the derived set; the resolver also
        // bumps FacetsProvenance to Explicit. The two together prove
        // the override flowed through ExtractFacetOverride →
        // RequirementInputResolver.Resolve(overrideFacets:) →
        // RequirementSetDeriver, which is the whole new wiring.
        var item = new WorkItemBuilder()
            .WithId(ApexId).WithType("Issue").WithTitle("Apex 4001").WithState("Doing")
            .WithTags($"polyphony;{PolyphonyTags.FacetsPrefix}={Facet.Plannable}")
            .Build();
        await SeedAsync(item);
        var runner = new FakeProcessRunner();
        BindBaseline(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Status.ShouldNotBe("error");

        var kinds = result.Requirements.Select(r => r.Kind).ToHashSet();
        kinds.ShouldNotContain(RequirementKind.ImplementationMerged);
        kinds.ShouldContain(RequirementKind.PlanAuthored);

        result.ResolvedInputs.Facets.ShouldBe([Facet.Plannable]);
        result.ResolvedInputs.FacetsProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    // ─── No tag → resolver falls back to type-config default ───────────

    [Fact]
    public async Task NextReady_NoFacetsTag_FallsBackToTypeConfigFacets()
    {
        // Pair to TaggedFacetsOverride: same fixture sans the tag must
        // produce the type-config default ([plannable, implementable]
        // for Issue) with FacetsProvenance=Default. Without this guard
        // a regression that always treated the override branch as the
        // hot path would only show up in an end-to-end harness.
        var item = new WorkItemBuilder()
            .WithId(ApexId).WithType("Issue").WithTitle("Apex 4001").WithState("Doing")
            .Build();
        await SeedAsync(item);
        var runner = new FakeProcessRunner();
        BindBaseline(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.Success);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Status.ShouldNotBe("error");

        var kinds = result.Requirements.Select(r => r.Kind).ToHashSet();
        kinds.ShouldContain(RequirementKind.ImplementationMerged);

        result.ResolvedInputs.Facets.ShouldContain(Facet.Plannable);
        result.ResolvedInputs.Facets.ShouldContain(Facet.Implementable);
        // Both the override branch and the type-config-declared branch
        // surface Explicit provenance (override is "architect said so"
        // and a populated type-config Facets is "operator said so").
        // The discriminator between the two is the facet content +
        // membership of implementation_merged in the requirement set,
        // both asserted above.
        result.ResolvedInputs.FacetsProvenance.ShouldBe(ResolutionProvenance.Explicit);
    }

    // ─── Malformed tag → routing-style error envelope on stdout ────────

    [Fact]
    public async Task NextReady_MalformedFacetsTag_EmitsErrorEnvelope_AndConfigErrorExitCode()
    {
        // An unknown facet token in the tag (e.g.
        // polyphony:facets=garbage) must not silently fall back to the
        // type-config default — that would mask architect typos and
        // produce work for the wrong facet set. ExtractFacetOverride
        // throws InvalidOperationException, the verb catches it at the
        // top of NextReady, and routes through EmitNextReadyError →
        // status=error envelope on stdout with
        // ExitCodes.ConfigError. The malformed token must surface in
        // the error string so the architect can fix the tag from the
        // CLI output alone.
        const string badToken = "garbage";
        var item = new WorkItemBuilder()
            .WithId(ApexId).WithType("Issue").WithTitle("Apex 4001").WithState("Doing")
            .WithTags($"{PolyphonyTags.FacetsPrefix}={badToken}")
            .Build();
        await SeedAsync(item);
        var runner = new FakeProcessRunner();
        BindBaseline(runner);

        var cmd = CreateCommand(runner);
        var (exit, output) = await CaptureConsoleAsync(() => cmd.NextReady(workItem: ApexId));
        exit.ShouldBe(ExitCodes.ConfigError);

        var result = JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.StateNextReadyResult)!;
        result.Status.ShouldBe("error");
        result.Error.ShouldNotBeNullOrWhiteSpace();
        result.Error!.ShouldContain(badToken, Case.Insensitive);
    }
}
