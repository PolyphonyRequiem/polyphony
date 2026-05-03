using Shouldly;
using Xunit;

namespace Polyphony.Tests;

/// <summary>
/// Tests for <see cref="ExitCodes"/> ensuring constant values match the documented
/// exit code scheme used by conductor shell scripts for workflow branching.
/// </summary>
public sealed class ExitCodesTests
{
    [Fact]
    public void Success_IsZero()
    {
        ExitCodes.Success.ShouldBe(0);
    }

    [Fact]
    public void RoutingFailure_IsOne()
    {
        ExitCodes.RoutingFailure.ShouldBe(1);
    }

    [Fact]
    public void ConfigError_IsTwo()
    {
        ExitCodes.ConfigError.ShouldBe(2);
    }

    [Fact]
    public void CacheError_IsThree()
    {
        ExitCodes.CacheError.ShouldBe(3);
    }

    [Fact]
    public void AllCodes_AreDistinct()
    {
        var codes = new[]
        {
            ExitCodes.Success,
            ExitCodes.RoutingFailure,
            ExitCodes.ConfigError,
            ExitCodes.CacheError,
            ExitCodes.HealthCheckFailed
        };

        codes.ShouldBeUnique();
    }

    [Theory]
    [InlineData(nameof(ExitCodes.Success), 0)]
    [InlineData(nameof(ExitCodes.RoutingFailure), 1)]
    [InlineData(nameof(ExitCodes.ConfigError), 2)]
    [InlineData(nameof(ExitCodes.CacheError), 3)]
    [InlineData(nameof(ExitCodes.HealthCheckFailed), 4)]
    public void ExitCode_MatchesDocumentedScheme(string name, int expected)
    {
        var actual = name switch
        {
            nameof(ExitCodes.Success) => ExitCodes.Success,
            nameof(ExitCodes.RoutingFailure) => ExitCodes.RoutingFailure,
            nameof(ExitCodes.ConfigError) => ExitCodes.ConfigError,
            nameof(ExitCodes.CacheError) => ExitCodes.CacheError,
            nameof(ExitCodes.HealthCheckFailed) => ExitCodes.HealthCheckFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

        actual.ShouldBe(expected, $"ExitCodes.{name} should be {expected}");
    }
}
