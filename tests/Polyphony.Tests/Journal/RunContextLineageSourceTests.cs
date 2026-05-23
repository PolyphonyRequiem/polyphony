using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal;

/// <summary>
/// Verifies the post-W2 lineage-source contract on
/// <see cref="RunContext"/>: env-set → <c>env</c>, env-unset →
/// <c>manual_fallback</c> with the canonical prefix preserved.
/// </summary>
public sealed class RunContextLineageSourceTests
{
    [Fact]
    public void EnvSet_ReportsEnvironmentSource()
    {
        var ctx = new RunContext(_ => "01HQABCD0123456789MNPQRSTV");
        ctx.RunIdSource.ShouldBe(RunContext.Sources.Environment);
        ctx.HasManualLineage.ShouldBeFalse();
        ctx.RunId.ShouldBe("01HQABCD0123456789MNPQRSTV");
    }

    [Fact]
    public void EnvUnset_FallsBackToManualPrefix()
    {
        var ctx = new RunContext(_ => null);
        ctx.RunIdSource.ShouldBe(RunContext.Sources.ManualFallback);
        ctx.HasManualLineage.ShouldBeTrue();
        ctx.RunId.ShouldStartWith(RunContext.ManualLineagePrefix);
    }

    [Fact]
    public void EnvBlank_TreatedAsUnset()
    {
        var ctx = new RunContext(_ => "   ");
        ctx.RunIdSource.ShouldBe(RunContext.Sources.ManualFallback);
        ctx.HasManualLineage.ShouldBeTrue();
    }

    [Fact]
    public void ExplicitConstructor_SetsExplicitSource()
    {
        var ctx = new RunContext("explicit_test_value");
        ctx.RunIdSource.ShouldBe(RunContext.Sources.Explicit);
        ctx.RunId.ShouldBe("explicit_test_value");
    }

    [Fact]
    public void HasManualLineage_KeysOnPrefix_EvenForExplicit()
    {
        var ctx = new RunContext($"{RunContext.ManualLineagePrefix}imported_from_legacy");
        ctx.HasManualLineage.ShouldBeTrue();
    }
}
