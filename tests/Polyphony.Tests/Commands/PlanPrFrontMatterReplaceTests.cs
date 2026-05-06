using Polyphony.Commands;
using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Unit tests for <see cref="PlanPrFrontMatter.ReplaceSnapshotPreservingTail"/>.
/// The cascade-remedy verb (Phase 3 P9 step 2) calls this after a clean
/// rebase to refresh the PR's <c>ancestor_plan_generations</c> snapshot.
/// The tail-preservation contract is the heart of the feature: we must
/// never lose body content beneath the front-matter, regardless of line
/// endings or markdown syntax in the tail.
/// </summary>
public sealed class PlanPrFrontMatterReplaceTests
{
    private static IReadOnlyDictionary<string, int> Snapshot(params (string key, int value)[] entries)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (k, v) in entries) d[k] = v;
        return d;
    }

    [Fact]
    public void Present_FreshSnapshot_ReplacedWithRewrittenBody()
    {
        var body = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 1\n---\n\n## Plan body\n\nstuff";
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(
            body, Snapshot(("root", 2), ("5678", 1)));

        var replaced = result.ShouldBeOfType<FrontMatterReplacement.Replaced>();
        replaced.NewBody.ShouldContain("ancestor_plan_generations:");
        replaced.NewBody.ShouldContain("root: 2");
        replaced.NewBody.ShouldContain("\"5678\": 1");
        replaced.NewBody.ShouldContain("\n\n## Plan body\n\nstuff");
        // Old snapshot entry is gone.
        replaced.NewBody.ShouldNotContain("root: 1");
    }

    [Fact]
    public void Present_StaleSnapshot_NewValuesOverwriteOld()
    {
        var body = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 1\n  \"42\": 5\n---\n\nbody";
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(
            body, Snapshot(("root", 9)));

        var replaced = result.ShouldBeOfType<FrontMatterReplacement.Replaced>();
        replaced.NewBody.ShouldContain("root: 9");
        replaced.NewBody.ShouldNotContain("\"42\"");
    }

    [Fact]
    public void Absent_NoFrontMatter_ReturnsAbsent()
    {
        var body = "Just a regular plan body — no front-matter.";
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(
            body, Snapshot(("root", 1)));

        result.ShouldBeOfType<FrontMatterReplacement.Absent>();
    }

    [Fact]
    public void Empty_ReturnsAbsent()
    {
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(
            string.Empty, Snapshot(("root", 1)));

        result.ShouldBeOfType<FrontMatterReplacement.Absent>();
    }

    [Fact]
    public void Malformed_BadYaml_ReturnsMalformed()
    {
        // Quoted "yes" is rejected by ParseStrict for requests_parent_change.
        var body = "---\nrequests_parent_change: \"yes\"\n---\n\nbody";
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(
            body, Snapshot(("root", 1)));

        var malformed = result.ShouldBeOfType<FrontMatterReplacement.Malformed>();
        malformed.Reason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void TailWithCrlf_PreservedExactly()
    {
        // Body has a CRLF tail; the replacement must keep the tail bytes
        // unchanged (line endings included).
        var body = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 1\n---\r\n\r\n## Heading\r\n\r\nLine\r\n";
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(
            body, Snapshot(("root", 2)));

        var replaced = result.ShouldBeOfType<FrontMatterReplacement.Replaced>();
        // The tail is everything from "\r\n\r\n## Heading..." through the end.
        replaced.NewBody.ShouldEndWith("\r\n\r\n## Heading\r\n\r\nLine\r\n");
    }

    [Fact]
    public void TailWithCodeFences_PreservedExactly()
    {
        var tail = "\n\n## Plan\n\n```csharp\npublic void Foo() { }\n```\n\n```yaml\nfoo: bar\n```\n";
        var body = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 1\n---" + tail;
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(
            body, Snapshot(("root", 2)));

        var replaced = result.ShouldBeOfType<FrontMatterReplacement.Replaced>();
        replaced.NewBody.ShouldEndWith(tail);
    }

    [Fact]
    public void RequestsParentChangeTrue_SurvivesRewrite()
    {
        var body = "---\nrequests_parent_change: true\nancestor_plan_generations:\n  root: 1\n---\n\nbody";
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(
            body, Snapshot(("root", 2)));

        var replaced = result.ShouldBeOfType<FrontMatterReplacement.Replaced>();
        replaced.NewBody.ShouldContain("requests_parent_change: true");
        // Round-trip via strict parse to confirm the flag survived.
        var roundTrip = PlanPrFrontMatter.ParseStrict(replaced.NewBody);
        roundTrip.Status.ShouldBe(FrontMatterStatus.Present);
        roundTrip.RequestsParentChange.ShouldBeTrue();
        roundTrip.AncestorPlanGenerations["root"].ShouldBe(2);
    }

    [Fact]
    public void NewSnapshotKeysSortedDeterministically()
    {
        // Pass keys in scrambled order; emitted YAML should be ordinal-sorted.
        var body = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 1\n---\n";
        var snapshot = Snapshot(("zeta", 9), ("alpha", 1), ("9999", 2), ("root", 3));
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(body, snapshot);

        var replaced = result.ShouldBeOfType<FrontMatterReplacement.Replaced>();
        // Ordinal sort: "9999" < "alpha" < "root" < "zeta".
        var idx9999 = replaced.NewBody.IndexOf("\"9999\":", StringComparison.Ordinal);
        var idxAlpha = replaced.NewBody.IndexOf("alpha:", StringComparison.Ordinal);
        var idxRoot = replaced.NewBody.IndexOf("root:", StringComparison.Ordinal);
        var idxZeta = replaced.NewBody.IndexOf("zeta:", StringComparison.Ordinal);
        idx9999.ShouldBeGreaterThan(0);
        idx9999.ShouldBeLessThan(idxAlpha);
        idxAlpha.ShouldBeLessThan(idxRoot);
        idxRoot.ShouldBeLessThan(idxZeta);
    }

    [Fact]
    public void FrontMatterOnly_NoTail_ReplacedWithEmptyTail()
    {
        var body = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 1\n---\n";
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(
            body, Snapshot(("root", 2)));

        var replaced = result.ShouldBeOfType<FrontMatterReplacement.Replaced>();
        // The new body must end with the closing fence + newline; no extra tail content.
        replaced.NewBody.ShouldEndWith("---\n");
        // Nothing after the second --- (i.e. the tail was empty and stays empty).
        var idxClose = replaced.NewBody.LastIndexOf("---\n", StringComparison.Ordinal);
        replaced.NewBody[(idxClose + 4)..].ShouldBeEmpty();
    }

    [Fact]
    public void TailContainingHorizontalRuleDashes_NotTruncated()
    {
        // Markdown horizontal rules ("---" on their own line) appear AFTER
        // the front-matter; the non-greedy regex must only consume the FIRST
        // closing fence so the rest of the body is preserved.
        var tail = "\n\nIntro\n\n---\n\nMore content\n\n---\n\nFinal\n";
        var body = "---\nrequests_parent_change: false\nancestor_plan_generations:\n  root: 1\n---" + tail;
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(
            body, Snapshot(("root", 2)));

        var replaced = result.ShouldBeOfType<FrontMatterReplacement.Replaced>();
        replaced.NewBody.ShouldEndWith(tail);
        replaced.NewBody.ShouldContain("Final\n");
    }

    [Fact]
    public void RoundTrip_StrictParseOfRewrittenBody_MatchesSnapshot()
    {
        // Belt-and-braces: the rewritten body must itself parse strictly
        // back to Present with the values we asked for.
        var body = "---\nrequests_parent_change: true\nancestor_plan_generations:\n  root: 1\n---\n\ntail content";
        var snapshot = Snapshot(("root", 5), ("100", 2), ("200", 1));
        var result = PlanPrFrontMatter.ReplaceSnapshotPreservingTail(body, snapshot);

        var replaced = result.ShouldBeOfType<FrontMatterReplacement.Replaced>();
        var roundTrip = PlanPrFrontMatter.ParseStrict(replaced.NewBody);
        roundTrip.Status.ShouldBe(FrontMatterStatus.Present);
        roundTrip.RequestsParentChange.ShouldBeTrue();
        roundTrip.AncestorPlanGenerations.Count.ShouldBe(3);
        roundTrip.AncestorPlanGenerations["root"].ShouldBe(5);
        roundTrip.AncestorPlanGenerations["100"].ShouldBe(2);
        roundTrip.AncestorPlanGenerations["200"].ShouldBe(1);
    }

    [Fact]
    public void RejectsNullSnapshot()
    {
        var body = "---\nrequests_parent_change: false\n---\n";
        Should.Throw<ArgumentNullException>(() =>
            PlanPrFrontMatter.ReplaceSnapshotPreservingTail(body, null!));
    }
}
