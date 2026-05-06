using Polyphony.Infrastructure.AzureDevOps;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.AzureDevOps;

public sealed class AdoTokenResolverTests
{
    [Fact]
    public void Resolve_ReturnsExtPat_WhenSet()
    {
        var resolver = new AdoTokenResolver(
            envReader: name => name == AdoTokenResolver.AzureDevOpsExtPatVar ? "ext-token" : null,
            precedence: [
                AdoTokenResolver.AzureDevOpsExtPatVar,
                AdoTokenResolver.AzureDevOpsPatVar,
                AdoTokenResolver.SystemAccessTokenVar,
            ]);

        resolver.Resolve().ShouldBe("ext-token");
    }

    [Fact]
    public void Resolve_PrefersExtPat_OverLegacyAndSystemAccessToken()
    {
        var resolver = new AdoTokenResolver(
            envReader: name => name switch
            {
                AdoTokenResolver.AzureDevOpsExtPatVar => "ext-token",
                AdoTokenResolver.AzureDevOpsPatVar => "legacy-token",
                AdoTokenResolver.SystemAccessTokenVar => "system-token",
                _ => null,
            },
            precedence: [
                AdoTokenResolver.AzureDevOpsExtPatVar,
                AdoTokenResolver.AzureDevOpsPatVar,
                AdoTokenResolver.SystemAccessTokenVar,
            ]);

        resolver.Resolve().ShouldBe("ext-token");
    }

    [Fact]
    public void Resolve_FallsBackToLegacyPat_WhenExtPatMissing()
    {
        var resolver = new AdoTokenResolver(
            envReader: name => name switch
            {
                AdoTokenResolver.AzureDevOpsPatVar => "legacy-token",
                AdoTokenResolver.SystemAccessTokenVar => "system-token",
                _ => null,
            },
            precedence: [
                AdoTokenResolver.AzureDevOpsExtPatVar,
                AdoTokenResolver.AzureDevOpsPatVar,
                AdoTokenResolver.SystemAccessTokenVar,
            ]);

        resolver.Resolve().ShouldBe("legacy-token");
    }

    [Fact]
    public void Resolve_FallsBackToSystemAccessToken_WhenOthersMissing()
    {
        var resolver = new AdoTokenResolver(
            envReader: name => name == AdoTokenResolver.SystemAccessTokenVar ? "pipeline-token" : null,
            precedence: [
                AdoTokenResolver.AzureDevOpsExtPatVar,
                AdoTokenResolver.AzureDevOpsPatVar,
                AdoTokenResolver.SystemAccessTokenVar,
            ]);

        resolver.Resolve().ShouldBe("pipeline-token");
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoVarsSet()
    {
        var resolver = new AdoTokenResolver(
            envReader: _ => null,
            precedence: [
                AdoTokenResolver.AzureDevOpsExtPatVar,
                AdoTokenResolver.AzureDevOpsPatVar,
                AdoTokenResolver.SystemAccessTokenVar,
            ]);

        resolver.Resolve().ShouldBeNull();
    }

    [Fact]
    public void Resolve_TreatsWhitespaceAsUnset_AndFallsThrough()
    {
        // A stray "AZURE_DEVOPS_EXT_PAT=  " export must not mask a real legacy PAT.
        var resolver = new AdoTokenResolver(
            envReader: name => name switch
            {
                AdoTokenResolver.AzureDevOpsExtPatVar => "   ",
                AdoTokenResolver.AzureDevOpsPatVar => "real-token",
                _ => null,
            },
            precedence: [
                AdoTokenResolver.AzureDevOpsExtPatVar,
                AdoTokenResolver.AzureDevOpsPatVar,
            ]);

        resolver.Resolve().ShouldBe("real-token");
    }

    [Fact]
    public void Constructor_RejectsEmptyPrecedence()
    {
        Should.Throw<ArgumentException>(() =>
            new AdoTokenResolver(envReader: _ => null, precedence: []));
    }

    [Fact]
    public void Constructor_RejectsNullEnvReader()
    {
        Should.Throw<ArgumentNullException>(() =>
            new AdoTokenResolver(envReader: null!, precedence: ["X"]));
    }

    [Fact]
    public void DefaultConstructor_ReadsActualEnvironment()
    {
        const string varName = AdoTokenResolver.AzureDevOpsExtPatVar;
        var prior = Environment.GetEnvironmentVariable(varName);
        try
        {
            Environment.SetEnvironmentVariable(varName, "probe-value");
            var resolver = new AdoTokenResolver();
            resolver.Resolve().ShouldBe("probe-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, prior);
        }
    }
}
