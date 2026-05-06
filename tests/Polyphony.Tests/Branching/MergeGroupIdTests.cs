using Polyphony.Branching;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Branching;

public sealed class MergeGroupIdTests
{
    [Theory]
    [InlineData("a")]                                 // minimal
    [InlineData("data-layer")]                        // hyphen-internal
    [InlineData("api2")]                              // alphanumeric
    [InlineData("auth")]                              // canonical example from ADR
    [InlineData("migrations")]                        // ADR example
    [InlineData("data-layer-migrations")]             // ADR example (the one Rev 3 broke on)
    [InlineData("abcdefghijklmnopqrstuvwxyz0-123")] // exactly 31 chars (max length)
    public void Parse_ValidGrammar_RoundTrips(string value)
    {
        var id = MergeGroupId.Parse(value);

        id.Value.ShouldBe(value);
        id.ToString().ShouldBe(value);
    }

    [Theory]
    [InlineData("")]                                                // empty
    [InlineData("A")]                                               // uppercase
    [InlineData("Auth")]                                            // mixed case
    [InlineData("1auth")]                                           // starts with digit
    [InlineData("-auth")]                                           // starts with hyphen
    [InlineData("auth_layer")]                                      // contains underscore (the hierarchy delimiter)
    [InlineData("auth/layer")]                                      // contains slash
    [InlineData("auth layer")]                                      // contains space
    [InlineData("authénticate")]                                    // non-ASCII
    [InlineData("abcdefghijklmnopqrstuvwxyz0-1234")]              // 32 chars (one over)
    public void Parse_InvalidGrammar_Throws(string value)
    {
        Should.Throw<FormatException>(() => MergeGroupId.Parse(value));
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Should.Throw<FormatException>(() => MergeGroupId.Parse(null!));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("data-layer")]
    public void TryParse_Valid_ReturnsTrue(string value)
    {
        var ok = MergeGroupId.TryParse(value, out var id);

        ok.ShouldBeTrue();
        id.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bad")]
    [InlineData("auth_layer")]
    public void TryParse_Invalid_ReturnsFalseAndDefault(string? value)
    {
        var ok = MergeGroupId.TryParse(value, out var id);

        ok.ShouldBeFalse();
        id.ShouldBe(default(MergeGroupId));
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = MergeGroupId.Parse("auth");
        var b = MergeGroupId.Parse("auth");

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void GrammarPattern_MatchesAdrSpecification()
    {
        // Sanity guard: the ADR's published grammar string and the constant
        // exposed here must stay in lockstep.
        MergeGroupId.GrammarPattern.ShouldBe("^[a-z][a-z0-9-]{0,30}$");
    }

    [Fact]
    public void MaxLength_MatchesAdrSpecification()
    {
        MergeGroupId.MaxLength.ShouldBe(31);
    }
}
