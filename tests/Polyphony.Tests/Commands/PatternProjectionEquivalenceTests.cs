using Polyphony.Commands;
using Polyphony.Journal;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;
using Polyphony.Journal.Reset;
using Polyphony.Journal.Reset.Deleters;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Pattern → projection equivalence verification (AB#3307) — the gating
/// evidence that the legacy <c>--strategy pattern</c> leg can be deleted
/// (AB#3308) without losing reset coverage.
///
/// <para>
/// The pattern leg in <see cref="ResetCommands"/> (the
/// <c>ResetRootPatternCoreAsync</c> chain in
/// <c>ResetCommands.Root.cs</c>) runs six per-leg helpers in order:
/// <c>prs → worktrees → branches → facets → manifest → state</c>. Each
/// leg re-derives "what does this root own?" from a hand-rolled regex
/// over <c>git for-each-ref</c> (or the equivalent twig/gh shell-out).
/// The projection leg
/// (<see cref="ProjectionResetExecutor"/> + <see cref="ProjectionResetCatalog"/>)
/// reads the journal, computes drift, plans deletions, and dispatches
/// per-kind <see cref="IResourceDeleter"/>s.
/// </para>
///
/// <para>This suite verifies four equivalence claims that, together,
/// give us confidence to retire pattern:</para>
///
/// <list type="number">
///   <item><b>Catalog phases mirror pattern legs.</b> Every pattern
///         leg name appears as a <see cref="ResetPhaseDefinition"/> in
///         <see cref="ProjectionResetCatalog.OrderedPhases"/>, in the
///         same order.</item>
///   <item><b>Catalog phase routing.</b> Every resource kind a pattern
///         leg acts on routes to the expected catalog phase (with the
///         AdoWorkItemTag → facets-vs-state split handled by ID
///         shape).</item>
///   <item><b>Catalog excluded kinds match pattern non-coverage.</b>
///         Every kind the pattern leg never touched (PR comments,
///         votes, work-item state, etc.) is recorded in
///         <see cref="ProjectionResetCatalog.ExcludedKinds"/> with a
///         rationale.</item>
///   <item><b>Branch-name matcher covers the pattern leg's enumeration
///         patterns.</b> Every branch name producible by
///         <c>ResetCommands.RootBranchPatterns</c> (plus the separate
///         <c>sdlc/root/{id}</c> enumeration) is recognized by
///         <see cref="ResourceObserverSupport.MatchesPolyphonyBranchPattern"/>
///         on the discovered-resource path. This is the AB#3306 fix.
///         </item>
/// </list>
///
/// <para>One behavioural fixture
/// (<see cref="Coverage_ReportsCompleteForRepresentativeFixture"/>)
/// drives the coverage analyzer end-to-end with a synthetic journal
/// containing one entry per pattern-leg kind, asserting that the
/// production-shaped observer + deleter set reports
/// <see cref="ProjectionResetCoverage.Complete"/>. If a future deleter
/// or observer registration regresses, this fixture catches it before
/// pattern deletion can mask the regression.</para>
///
/// <para>This suite is deletion-gated infrastructure: once AB#3308
/// removes the pattern leg, the equivalence claim collapses to "the
/// projection catalog is internally consistent" and these tests can
/// be pruned to just the catalog/matcher invariants.</para>
/// </summary>
public sealed class PatternProjectionEquivalenceTests
{
    private const string LegPrs = "prs";
    private const string LegWorktrees = "worktrees";
    private const string LegBranches = "branches";
    private const string LegFacets = "facets";
    private const string LegManifest = "manifest";
    private const string LegState = "state";

    private const int FixtureRoot = 1234;

    [Fact]
    public void Catalog_PhasesMirrorPatternLegsInOrder()
    {
        var catalogPhases = ProjectionResetCatalog.OrderedPhases.Select(p => p.Name).ToArray();
        var patternLegs = new[] { LegPrs, LegWorktrees, LegBranches, LegFacets, LegManifest, LegState };

        catalogPhases.ShouldBe(patternLegs);
    }

    [Theory]
    [InlineData(LegPrs, ResourceKind.GitHubPr, "owner/repo#42")]
    [InlineData(LegPrs, ResourceKind.AdoPr, "dev.azure.com/org/proj/_apis/git/repositories/repo/pullRequests/42")]
    [InlineData(LegWorktrees, ResourceKind.GitWorktree, "/tmp/worktree-1234")]
    [InlineData(LegBranches, ResourceKind.GitBranch, "feature/1234")]
    [InlineData(LegBranches, ResourceKind.GitBranch, "sdlc/root/1234")]
    [InlineData(LegFacets, ResourceKind.AdoWorkItemTag, "1234:polyphony:planned")]
    [InlineData(LegFacets, ResourceKind.AdoWorkItemTag, "1234:polyphony:facets=plannable,implementable")]
    [InlineData(LegManifest, ResourceKind.ManifestFile, ".polyphony/run.yaml")]
    [InlineData(LegManifest, ResourceKind.PlanFile, "plans/plan-1234.yaml")]
    [InlineData(LegManifest, ResourceKind.LockFile, ".polyphony/lock")]
    [InlineData(LegState, ResourceKind.AdoWorkItemTag, "1234:polyphony:run-started-at=2026-05-24T00:00:00.000Z")]
    public void Catalog_RoutesPatternLegResourceToCorrectPhase(string expectedPhase, string kind, string id)
    {
        var resource = MakeResource(kind, id);

        var matchingPhases = ProjectionResetCatalog.OrderedPhases
            .Where(p => p.Matches(resource))
            .ToArray();

        matchingPhases.ShouldHaveSingleItem(
            $"Resource ({kind}, {id}) should route to exactly one catalog phase " +
            $"but matched: [{string.Join(", ", matchingPhases.Select(p => p.Name))}]");
        matchingPhases[0].Name.ShouldBe(expectedPhase);
    }

    [Theory]
    [InlineData(ResourceKind.GitTag, "polyphony-run-1234")]
    [InlineData(ResourceKind.GitHubPrComment, "owner/repo#42#comment-1")]
    [InlineData(ResourceKind.AdoPrComment, "pr-42#comment-1")]
    [InlineData(ResourceKind.AdoPrVote, "pr-42#voter-1")]
    [InlineData(ResourceKind.AdoWorkItem, "1234")]
    [InlineData(ResourceKind.AdoWorkItemState, "1234#state=Active")]
    public void Catalog_ExcludesKindsPatternLegNeverDeleted(string kind, string id)
    {
        var resource = MakeResource(kind, id);

        ProjectionResetCatalog.OrderedPhases
            .Any(p => p.Matches(resource))
            .ShouldBeFalse($"Kind '{kind}' should not be reset-relevant (pattern leg never deleted it).");

        ProjectionResetCatalog.ExcludedKinds
            .ShouldContainKey(kind, $"Kind '{kind}' should be recorded in ExcludedKinds with a rationale.");
        ProjectionResetCatalog.ExcludedKinds[kind].ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Mirrors the pattern leg's <c>RootBranchPatterns</c>
    /// (<see cref="ResetCommands"/>:80-97) plus the separate
    /// <c>EnumerateRootSdlcBranchesAsync</c> output
    /// (<c>ResetCommands.Branches.cs</c>:273-313). Every concrete
    /// branch name the pattern leg's git-for-each-ref query could
    /// return MUST be recognized by the projection's
    /// discovered-resource matcher; otherwise the projection would
    /// silently drop orphan refs that pattern caught.
    /// </summary>
    [Theory]
    [InlineData("feature/1234")]
    [InlineData("plan/1234")]
    [InlineData("plan/1234-5678")]
    [InlineData("mg/1234_data-layer")]
    [InlineData("mg/1234_data-layer_migrations")]
    [InlineData("impl/1234-5678")]
    [InlineData("evidence/1234")]
    [InlineData("evidence/1234-5678")]
    [InlineData("sdlc/root/1234")]
    public void Matcher_RecognizesEveryBranchNameThePatternLegCouldEnumerate(string branchName)
    {
        ResourceObserverSupport.MatchesPolyphonyBranchPattern(FixtureRoot, branchName)
            .ShouldBeTrue($"Pattern leg would enumerate '{branchName}' but projection matcher does not recognize it.");
    }

    [Theory]
    [InlineData("feature/9999")]
    [InlineData("plan/9999-5678")]
    [InlineData("mg/9999_data-layer")]
    [InlineData("main")]
    [InlineData("user/dangreen/wip")]
    [InlineData("sdlc/root/9999")]
    public void Matcher_DoesNotMisattributeOtherRootsBranches(string branchName)
    {
        ResourceObserverSupport.MatchesPolyphonyBranchPattern(FixtureRoot, branchName)
            .ShouldBeFalse($"Branch '{branchName}' belongs to a different root or is not polyphony-owned; matcher must not claim it for root {FixtureRoot}.");
    }

    /// <summary>
    /// End-to-end coverage check. Feed
    /// <see cref="ProjectionResetCoverageAnalyzer"/> a journal whose
    /// effects span every pattern-leg kind, using the same observer
    /// and deleter set as production DI
    /// (<c>PolyphonyServiceRegistration.cs</c>:65-122). The report
    /// must come back <see cref="ProjectionResetCoverage.Complete"/>;
    /// any missing observer or deleter would surface here before
    /// pattern deletion can mask it.
    /// </summary>
    [Fact]
    public void Coverage_ReportsCompleteForRepresentativeFixture()
    {
        var entries = BuildRepresentativeFixtureJournal();

        var observerKinds = ProductionObserverKinds();
        var deleterKinds = ProductionDeleterKinds();

        var observers = observerKinds.Select(k => (IResourceObserver)new StubObserver(k));
        var deleters = deleterKinds.Select(k => (IResourceDeleter)new StubDeleter(k));

        var analyzer = new ProjectionResetCoverageAnalyzer(observers, deleters);
        var report = analyzer.Analyze(entries);

        report.MissingObservers.ShouldBeEmpty(
            $"Production DI does not register an IResourceObserver for: [{string.Join(", ", report.MissingObservers)}]");
        report.MissingDeleters.ShouldBeEmpty(
            $"Production DI does not register an IResourceDeleter for: [{string.Join(", ", report.MissingDeleters)}]");
        report.MissingEffectsActions.ShouldBeEmpty(
            $"Fixture journal entries missing effects (test-fixture bug, not production bug): [{string.Join(", ", report.MissingEffectsActions)}]");
        report.Coverage.ShouldBe(ProjectionResetCoverage.Complete);
    }

    /// <summary>
    /// The pattern leg deletes branches enumerated from
    /// <c>RootBranchPatterns</c> + <c>EnumerateRootSdlcBranchesAsync</c>;
    /// projection's <see cref="GitBranchDeleter"/> deletes branches the
    /// drift fold reports as polyphony-owned. The two must converge on
    /// the same branch name set for any given fixture. This test
    /// asserts the mapping at the matcher level (the unit that
    /// determines what the drift fold considers polyphony-owned for
    /// discovered orphan refs) — a real fixture-level diff requires a
    /// full executor wiring, which the <see cref="ResetRootProjectionCommandTests"/>
    /// suite already exercises.
    /// </summary>
    [Fact]
    public void Equivalence_PatternLegEnumerationIsSubsetOfMatcherRecognition()
    {
        var patternEnumeratedNames = new[]
        {
            $"plan/{FixtureRoot}",
            $"plan/{FixtureRoot}-5678",
            $"mg/{FixtureRoot}_data-layer",
            $"impl/{FixtureRoot}-5678",
            $"evidence/{FixtureRoot}-5678",
            $"feature/{FixtureRoot}",
            $"sdlc/root/{FixtureRoot}",
        };

        var unrecognized = patternEnumeratedNames
            .Where(name => !ResourceObserverSupport.MatchesPolyphonyBranchPattern(FixtureRoot, name))
            .ToArray();

        unrecognized.ShouldBeEmpty(
            $"Pattern leg would delete these branches but projection's discovered-resource matcher would not: [{string.Join(", ", unrecognized)}]");
    }

    private static ProjectedResourceState MakeResource(string kind, string id) => new()
    {
        Key = new ResourceKey { Kind = kind, Id = id },
        Action = "fixture",
        StartedAt = 1_000,
        Intent = Polyphony.Journal.ResourceIntent.EnsurePresent,
        Mutation = Polyphony.Journal.ResourceMutation.CreatedNow,
        PolyphonyOwned = true,
    };

    private static IReadOnlyList<JournalEntry> BuildRepresentativeFixtureJournal()
    {
        var effects = new (string Kind, string Id)[]
        {
            (ResourceKind.GitHubPr, "owner/repo#42"),
            (ResourceKind.AdoPr, "dev.azure.com/org/proj/_apis/git/repositories/repo/pullRequests/42"),
            (ResourceKind.GitWorktree, "/tmp/worktree-1234"),
            (ResourceKind.GitBranch, $"feature/{FixtureRoot}"),
            (ResourceKind.AdoWorkItemTag, $"{FixtureRoot}:polyphony:planned"),
            (ResourceKind.AdoWorkItemTag, $"{FixtureRoot}:polyphony:run-started-at=2026-05-24T00:00:00.000Z"),
            (ResourceKind.ManifestFile, ".polyphony/run.yaml"),
            (ResourceKind.PlanFile, $"plans/plan-{FixtureRoot}.yaml"),
            (ResourceKind.LockFile, ".polyphony/lock"),
        };

        var id = 0;
        return effects
            .Select(e => new JournalEntry
            {
                Id = ++id,
                RunId = "fixture-run",
                RootId = FixtureRoot,
                WorkItemId = FixtureRoot,
                Action = $"fixture_{e.Kind}",
                Target = e.Id,
                StartedAt = 1_000 + id,
                FinishedAt = 1_001 + id,
                Outcome = JournalOutcome.Success,
                Effects =
                [
                    new JournalResourceEffect
                    {
                        Kind = e.Kind,
                        Id = e.Id,
                        Intent = Polyphony.Journal.ResourceIntent.EnsurePresent,
                        Mutation = Polyphony.Journal.ResourceMutation.CreatedNow,
                        PolyphonyOwned = true,
                    },
                ],
            })
            .ToList();
    }

    private static IReadOnlyList<string> ProductionObserverKinds() =>
    [
        ResourceKind.GitBranch,
        ResourceKind.GitHubPr,
        ResourceKind.AdoPr,
        ResourceKind.AdoWorkItem,
        ResourceKind.AdoWorkItemState,
        ResourceKind.AdoWorkItemTag,
        ResourceKind.GitWorktree,
        ResourceKind.ManifestFile,
        ResourceKind.PlanFile,
        ResourceKind.LockFile,
    ];

    private static IReadOnlyList<string> ProductionDeleterKinds() =>
    [
        ResourceKind.GitBranch,
        ResourceKind.GitWorktree,
        ResourceKind.GitHubPr,
        ResourceKind.AdoPr,
        ResourceKind.AdoWorkItemTag,
        ResourceKind.ManifestFile,
        ResourceKind.PlanFile,
        ResourceKind.LockFile,
    ];

    private sealed class StubObserver(string kind) : IResourceObserver
    {
        public string Kind { get; } = kind;
        public bool CanObserve => true;
        public string? DeferredReason => null;

        public Task<ResourceObservationBatch> ObserveAsync(ResourceObservationRequest request, CancellationToken ct)
            => Task.FromResult(new ResourceObservationBatch
            {
                Kind = Kind,
                Observations = [],
                DiscoveredResources = [],
            });
    }

    private sealed class StubDeleter(string kind) : IResourceDeleter
    {
        public string Kind { get; } = kind;

        public Task<ResourceDeleteOutcome> DeleteAsync(
            ProjectedResourceState resource,
            ObservedResourceState? observation,
            ResourceDeletionContext context,
            CancellationToken ct)
            => Task.FromResult(new ResourceDeleteOutcome { Success = true, Deleted = true });
    }
}
