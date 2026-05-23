using System;
using Polyphony.Commands;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// W6 (AB#3280): unit tests for the PrBodyMarker helper. Round-trip
/// tests against the real PR-opening verbs live in the per-verb test
/// classes (PrCommandsOpenImplPr*, PrCommandsCreateFeatureAdo*, etc.);
/// this class covers the helper itself in isolation.
/// </summary>
public sealed class PrBodyMarkerTests
{
    private const string SampleRunId = "01HZK7Y9ABCDEF0123456789AB";
    private const string SecondRunId = "01HZK8AAABCDEF0123456789AB";

    [Fact]
    public void Format_Canonical_EmitsHtmlComment()
    {
        PrBodyMarker.Format(SampleRunId)
            .ShouldBe($"<!-- polyphony:run_id={SampleRunId} -->");
    }

    [Fact]
    public void Format_EmptyRunId_Throws()
    {
        Should.Throw<ArgumentException>(() => PrBodyMarker.Format(""));
    }

    [Fact]
    public void EnsureRunIdPrefix_EmptyRunId_ReturnsBodyUnchanged()
    {
        var body = "## Summary\n\nDoing the thing.";
        PrBodyMarker.EnsureRunIdPrefix(body, null).ShouldBe(body);
        PrBodyMarker.EnsureRunIdPrefix(body, "").ShouldBe(body);
        PrBodyMarker.EnsureRunIdPrefix(body, "   ").ShouldBe(body);
    }

    [Fact]
    public void EnsureRunIdPrefix_NoExistingMarker_PrependsMarkerAndNewline()
    {
        var body = "## Summary\n\nDoing the thing.";
        var result = PrBodyMarker.EnsureRunIdPrefix(body, SampleRunId);
        result.ShouldStartWith($"<!-- polyphony:run_id={SampleRunId} -->");
        result.ShouldEndWith(body);
        result.Length.ShouldBe(body.Length + PrBodyMarker.Format(SampleRunId).Length + Environment.NewLine.Length);
    }

    [Fact]
    public void EnsureRunIdPrefix_AlreadyHasMarker_ReturnsUnchanged()
    {
        var body = PrBodyMarker.Format(SampleRunId) + "\n## Summary\n";
        PrBodyMarker.EnsureRunIdPrefix(body, SampleRunId).ShouldBe(body);
    }

    [Fact]
    public void EnsureRunIdPrefix_AlreadyHasForeignMarker_DoesNotOverwrite()
    {
        // The body already claims a different lineage. Refusing to
        // overwrite is the correct posture — W9 owns the foreign-PR
        // detection question; the body-marker writer must not lie.
        var body = PrBodyMarker.Format(SecondRunId) + "\n## Summary\n";
        PrBodyMarker.EnsureRunIdPrefix(body, SampleRunId).ShouldBe(body);
    }

    [Fact]
    public void EnsureRunIdPrefix_NullBody_ReturnsJustTheMarker()
    {
        // Defensive: callers occasionally pass a null body when the
        // upstream default-body resolver was skipped on an error path.
        // Returning just the marker keeps the stamp present without
        // crashing.
        PrBodyMarker.EnsureRunIdPrefix(null!, SampleRunId)
            .ShouldBe(PrBodyMarker.Format(SampleRunId));
    }

    [Fact]
    public void TryParseRunId_Empty_ReturnsNull()
    {
        PrBodyMarker.TryParseRunId(null).ShouldBeNull();
        PrBodyMarker.TryParseRunId("").ShouldBeNull();
    }

    [Fact]
    public void TryParseRunId_NoMarker_ReturnsNull()
    {
        PrBodyMarker.TryParseRunId("## Summary\n\nNo marker here.").ShouldBeNull();
    }

    [Fact]
    public void TryParseRunId_StampedBody_ReturnsRunId()
    {
        var body = PrBodyMarker.EnsureRunIdPrefix("## Summary\n", SampleRunId);
        PrBodyMarker.TryParseRunId(body).ShouldBe(SampleRunId);
    }

    [Fact]
    public void TryParseRunId_CaseInsensitive_Recognized()
    {
        var body = $"<!-- POLYPHONY:RUN_ID={SampleRunId} -->\n## Summary";
        PrBodyMarker.TryParseRunId(body).ShouldBe(SampleRunId);
    }

    [Fact]
    public void TryParseRunId_TolerantOfWhitespace()
    {
        var body = $"   <!--   polyphony:run_id  =  {SampleRunId}   -->\nbody";
        PrBodyMarker.TryParseRunId(body).ShouldBe(SampleRunId);
    }

    [Fact]
    public void RoundTrip_EnsurePlusParse_Idempotent()
    {
        var body = "## Summary\n\nbody";
        var stamped = PrBodyMarker.EnsureRunIdPrefix(body, SampleRunId);
        PrBodyMarker.TryParseRunId(stamped).ShouldBe(SampleRunId);
        // Second ensure call on a stamped body is a no-op.
        PrBodyMarker.EnsureRunIdPrefix(stamped, SampleRunId).ShouldBe(stamped);
        // After ensure, parsing still recovers the original id.
        PrBodyMarker.TryParseRunId(PrBodyMarker.EnsureRunIdPrefix(stamped, SampleRunId)).ShouldBe(SampleRunId);
    }
}
