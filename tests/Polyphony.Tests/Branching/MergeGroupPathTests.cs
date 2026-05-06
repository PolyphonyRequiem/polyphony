using Polyphony.Branching;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Branching;

public sealed class MergeGroupPathTests
{
    [Fact]
    public void Top1_SingleSegment_IsTopLevel()
    {
        var path = MergeGroupPath.Top1(MergeGroupId.Parse("auth"));

        path.IsTopLevel.ShouldBeTrue();
        path.Depth.ShouldBe(1);
        path.Top.Value.ShouldBe("auth");
        path.Terminal.Value.ShouldBe("auth");
        path.Canonical.ShouldBe("auth");
    }

    [Fact]
    public void Of_MultipleSegments_OrdersRootToLeaf()
    {
        var path = MergeGroupPath.Of(
            MergeGroupId.Parse("data-layer"),
            MergeGroupId.Parse("migrations"),
            MergeGroupId.Parse("schema"));

        path.IsTopLevel.ShouldBeFalse();
        path.Depth.ShouldBe(3);
        path.Top.Value.ShouldBe("data-layer");
        path.Terminal.Value.ShouldBe("schema");
        path.Canonical.ShouldBe("data-layer_migrations_schema");
        path.Segments.Select(s => s.Value).ShouldBe(["data-layer", "migrations", "schema"]);
    }

    [Fact]
    public void Of_Empty_Throws()
    {
        Should.Throw<ArgumentException>(() => MergeGroupPath.Of(Array.Empty<MergeGroupId>()));
    }

    [Fact]
    public void Of_Null_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            MergeGroupPath.Of(segments: (IEnumerable<MergeGroupId>)null!));
    }

    [Fact]
    public void Push_ReturnsNewPath_DoesNotMutateOriginal()
    {
        var original = MergeGroupPath.Top1(MergeGroupId.Parse("auth"));
        var pushed = original.Push(MergeGroupId.Parse("migrations"));

        original.Depth.ShouldBe(1);
        original.Canonical.ShouldBe("auth");

        pushed.Depth.ShouldBe(2);
        pushed.Canonical.ShouldBe("auth_migrations");
        pushed.Top.Value.ShouldBe("auth");
        pushed.Terminal.Value.ShouldBe("migrations");
    }

    [Theory]
    [InlineData("auth", 1)]
    [InlineData("auth_migrations", 2)]
    [InlineData("data-layer_migrations_schema", 3)]
    [InlineData("a_b_c_d_e", 5)]
    public void Parse_RoundTripsBuilder(string canonical, int expectedDepth)
    {
        var path = MergeGroupPath.Parse(canonical);

        path.Canonical.ShouldBe(canonical);
        path.Depth.ShouldBe(expectedDepth);
        path.ToString().ShouldBe(canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("_")]                       // empty terminal (and root)
    [InlineData("auth_")]                   // empty terminal
    [InlineData("_migrations")]             // empty root
    [InlineData("auth__migrations")]        // empty middle
    [InlineData("Auth")]                    // uppercase
    [InlineData("1auth")]                   // starts with digit
    [InlineData("auth_1bad")]               // bad nested
    [InlineData("auth/migrations")]         // contains slash
    [InlineData(" auth")]                   // whitespace
    public void Parse_Invalid_Throws(string? canonical)
    {
        Should.Throw<FormatException>(() => MergeGroupPath.Parse(canonical!));
    }

    [Fact]
    public void TryParse_Invalid_ReturnsFalseAndNull()
    {
        var ok = MergeGroupPath.TryParse("bad__path", out var path);

        ok.ShouldBeFalse();
        path.ShouldBeNull();
    }

    [Fact]
    public void Equality_UsesCanonicalString()
    {
        // Two paths constructed via different routes (Of vs Push vs Parse)
        // must compare equal when their canonical strings match.
        var fromOf = MergeGroupPath.Of(
            MergeGroupId.Parse("auth"),
            MergeGroupId.Parse("migrations"));
        var fromPush = MergeGroupPath.Top1(MergeGroupId.Parse("auth"))
            .Push(MergeGroupId.Parse("migrations"));
        var fromParse = MergeGroupPath.Parse("auth_migrations");

        fromOf.ShouldBe(fromPush);
        fromOf.ShouldBe(fromParse);
        fromPush.ShouldBe(fromParse);

        fromOf.GetHashCode().ShouldBe(fromPush.GetHashCode());
        fromOf.GetHashCode().ShouldBe(fromParse.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentSegments_AreUnequal()
    {
        var a = MergeGroupPath.Parse("auth_migrations");
        var b = MergeGroupPath.Parse("auth_schemas");
        var c = MergeGroupPath.Parse("auth");

        a.ShouldNotBe(b);
        a.ShouldNotBe(c);
    }

    [Fact]
    public void Equality_NullSafeOperators()
    {
        var a = MergeGroupPath.Parse("auth");
        MergeGroupPath? n = null;

        (a == null).ShouldBeFalse();
        (a != null).ShouldBeTrue();
        (n == null).ShouldBeTrue();
        (n != null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(1, false, false)]  // depth 1: no warning, no stop
    [InlineData(2, false, false)]  // depth 2: still under warn threshold
    [InlineData(3, true, false)]   // depth 3: warning per ADR
    [InlineData(4, true, false)]   // depth 4: still warning, not stop
    [InlineData(5, true, false)]   // depth 5: warning, hard-stop boundary (not exceeded)
    [InlineData(6, true, true)]    // depth 6: hard stop without override
    public void DepthGates_MatchAdrSpecification(int depth, bool expectWarn, bool expectStop)
    {
        // Build a path of `depth` segments using a unique-per-position id
        // so the canonical form is well-formed regardless of depth.
        var segments = Enumerable.Range(1, depth)
            .Select(i => MergeGroupId.Parse($"seg-{i}"))
            .ToArray();
        var path = MergeGroupPath.Of(segments);

        path.Depth.ShouldBe(depth);
        path.RequiresDepthWarning.ShouldBe(expectWarn);
        path.ExceedsDefaultHardStopDepth.ShouldBe(expectStop);
    }

    [Fact]
    public void DepthGateConstants_MatchAdrSpecification()
    {
        // ADR § Branch-name length cap & nesting depth:
        //   "Depth 3: warning emitted on materialization."
        //   "Depth 5: hard stop. Driver refuses with a clear error..."
        MergeGroupPath.WarningDepth.ShouldBe(3);
        MergeGroupPath.DefaultHardStopDepth.ShouldBe(5);
    }
}
