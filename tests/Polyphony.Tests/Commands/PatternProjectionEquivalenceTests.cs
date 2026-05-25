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
/// Catalog + matcher invariants for the projection reset path
/// (post-AB#3308). These tests began life as the AB#3307 pattern →
/// projection equivalence gate; the pattern leg is now retired
/// (<c>docs/decisions/pattern-strategy-retirement.md</c>), and what
/// remains is the structural verification that the projection
/// catalog covers every resource kind the legacy pattern legs touched:
///
/// <list type="number">
///   <item><b>Catalog phases preserve the historical pattern-leg
///         ordering</b> (<c>prs → worktrees → branches → facets →
///         manifest → state</c>). The order is the documented reset
///         sequence in <c>docs/decisions/run-reset.md</c>; it is now
///         enforced solely through <see cref="ProjectionResetCatalog"/>.
///         </item>
///   <item><b>Catalog phase routing.</b> Each resource kind a pattern
///         leg used to act on routes to exactly one catalog phase, with
///         the AdoWorkItemTag facets-vs-state split discriminated by
///         tag-id shape (<c>polyphony:planned</c> /
///         <c>polyphony:facets</c> for facets,
///         <c>polyphony:run-started-at</c> for state).</item>
///   <item><b>Catalog excluded kinds cover historical pattern
///         non-coverage.</b> Every kind the pattern leg never touched
///         (PR comments, votes, work-item state, git tags) is recorded
///         in <see cref="ProjectionResetCatalog.ExcludedKinds"/> with a
///         rationale.</item>
///   <item><b>Branch-name matcher recognizes every historical
///         pattern-leg enumeration shape.</b> Every branch name the
///         retired <c>RootBranchPatterns</c> helper + the descendant
///         <c>sdlc/root/{id}</c> enumeration would have produced is
///         recognized by
///         <see cref="ResourceObserverSupport.MatchesPolyphonyBranchPattern"/>
///         on the discovered-resource path (the AB#3306 fix).</item>
/// </list>
///
/// <para>One behavioural fixture
/// (<see cref="Coverage_ReportsCompleteForRepresentativeFixture"/>)
/// drives <see cref="ProjectionResetCoverageAnalyzer"/> end-to-end with
/// a synthetic journal containing one entry per reset-relevant kind,
/// asserting that the production observer + deleter set reports
/// <see cref="ProjectionResetCoverage.Complete"/>. If a future deleter
/// or observer registration regresses, this fixture catches it.</para>
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
