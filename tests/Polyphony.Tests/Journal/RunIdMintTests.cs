using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal;

/// <summary>
/// Smoke tests for the ULID mint that backs the lineage primitive (W2).
/// </summary>
public sealed class RunIdMintTests
{
    [Fact]
    public void NewRunId_ProducesCanonicalLength()
    {
        var id = RunIdMint.NewRunId();
        id.Length.ShouldBe(RunIdMint.UlidLength);
    }

    [Fact]
    public void NewRunId_UsesCrockfordAlphabetOnly()
    {
        var id = RunIdMint.NewRunId();
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        foreach (var c in id)
        {
            alphabet.ShouldContain(c);
        }
    }

    [Fact]
    public void NewRunId_UniqueAcrossManyDraws()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 1_000; i++)
        {
            set.Add(RunIdMint.NewRunId()).ShouldBeTrue("ULID collision within 1k samples — randomness is broken.");
        }
    }

    [Fact]
    public void NewRunId_FixedTimestamp_HasLexicographicPrefixOrdering()
    {
        var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var t1 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_500);

        for (var trial = 0; trial < 100; trial++)
        {
            var a = RunIdMint.NewRunId(t0);
            var b = RunIdMint.NewRunId(t1);
            string.CompareOrdinal(a, b).ShouldBeLessThan(0,
                $"Later timestamp should sort after earlier (a={a}, b={b}).");
        }
    }

    [Fact]
    public void Compose_ProducesStableEncoding_ForKnownInputs()
    {
        // 1234567890 ms past epoch, zero-randomness — deterministic.
        var t = DateTimeOffset.FromUnixTimeMilliseconds(1_234_567_890L);
        ReadOnlySpan<byte> randomness = stackalloc byte[10];
        var encoded = RunIdMint.Compose(t, randomness);
        encoded.Length.ShouldBe(RunIdMint.UlidLength);
        encoded.ShouldEndWith(new string('0', 16));
    }

    [Fact]
    public void IsWellFormed_AcceptsFreshMint()
    {
        RunIdMint.IsWellFormed(RunIdMint.NewRunId()).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ulid")]
    [InlineData("manual_deadbeef")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAI")] // contains 'I' — excluded from Crockford
    public void IsWellFormed_RejectsMalformed(string? value)
    {
        RunIdMint.IsWellFormed(value).ShouldBeFalse();
    }
}
