using Polyphony.Versioning;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Versioning;

/// <summary>
/// Pure unit tests for <see cref="PolyphonyVersion"/>: parsing, the
/// SemVer-core comparator, and YAML metadata extraction.
/// </summary>
public sealed class PolyphonyVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("10.20.30", 10, 20, 30)]
    [InlineData("1.0.0+sha.abcd", 1, 0, 0)]                    // strip build-metadata
    [InlineData("1.0.0-alpha.0.5", 1, 0, 0)]                    // strip prerelease
    [InlineData("1.2.3-alpha+sha", 1, 2, 3)]                    // strip both
    [InlineData("  1.0.0  ", 1, 0, 0)]                          // tolerate whitespace
    [InlineData("1.0", 1, 0, 0)]                                // patch defaults to 0
    public void ParseCore_ValidInputs_ReturnsExpectedTuple(string input, int major, int minor, int patch)
    {
        var result = PolyphonyVersion.ParseCore(input);
        result.ShouldNotBeNull();
        result.Value.Major.ShouldBe(major);
        result.Value.Minor.ShouldBe(minor);
        result.Value.Patch.ShouldBe(patch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v1.0.0")]                                       // leading 'v' is NOT SemVer
    [InlineData("1.2.3.4")]                                      // 4-part dotted is NOT SemVer
    [InlineData("1.x.0")]                                        // non-numeric segment
    public void ParseCore_InvalidInputs_ReturnsNull(string? input)
    {
        PolyphonyVersion.ParseCore(input).ShouldBeNull();
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("2.0.0", "1.0.0", true)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.0", "1.1.0", false)]
    [InlineData("1.0.0", "2.0.0", false)]
    // Pre-release suffix is intentionally ignored — see class docs.
    [InlineData("1.0.0-alpha.0.5", "1.0.0", true)]
    [InlineData("1.0.0+abc", "1.0.0", true)]
    public void Satisfies_CoreComparison_ReturnsExpectedResult(string current, string required, bool expected)
    {
        var ok = PolyphonyVersion.Satisfies(current, required, out var reason);
        ok.ShouldBe(expected);
        if (expected)
        {
            reason.ShouldBeNull();
        }
        else
        {
            reason.ShouldNotBeNull();
            reason.ShouldContain(current);
            reason.ShouldContain(required);
        }
    }

    [Fact]
    public void Satisfies_UnparsableCurrent_ReturnsFalseWithReason()
    {
        var ok = PolyphonyVersion.Satisfies("not-semver", "1.0.0", out var reason);
        ok.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason.ShouldContain("current");
    }

    [Fact]
    public void Satisfies_UnparsableRequired_ReturnsFalseWithReason()
    {
        var ok = PolyphonyVersion.Satisfies("1.0.0", "garbage", out var reason);
        ok.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason.ShouldContain("required");
    }

    [Fact]
    public void ReadMinVersionFromWorkflow_ValidYaml_ReturnsValue()
    {
        var path = WriteTempYaml("""
            workflow:
              name: test
              version: "1.2.3"
              metadata:
                min_polyphony_version: "1.2.3"
                some_other_key: ignored
            agents: []
            """);
        try
        {
            PolyphonyVersion.ReadMinVersionFromWorkflow(path).ShouldBe("1.2.3");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadMinVersionFromWorkflow_NoMetadataBlock_ReturnsNull()
    {
        var path = WriteTempYaml("""
            workflow:
              name: test
              version: "1.0.0"
            agents: []
            """);
        try
        {
            PolyphonyVersion.ReadMinVersionFromWorkflow(path).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadMinVersionFromWorkflow_MetadataWithoutMinVersion_ReturnsNull()
    {
        var path = WriteTempYaml("""
            workflow:
              name: test
              version: "1.0.0"
              metadata:
                owner: someone
            agents: []
            """);
        try
        {
            PolyphonyVersion.ReadMinVersionFromWorkflow(path).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadMinVersionFromWorkflow_MissingFile_ReturnsNull()
    {
        var bogus = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.yaml");
        PolyphonyVersion.ReadMinVersionFromWorkflow(bogus).ShouldBeNull();
    }

    [Fact]
    public void GetCurrent_ReturnsNonEmptyVersion()
    {
        var v = PolyphonyVersion.GetCurrent();
        v.ShouldNotBeNullOrWhiteSpace();
    }

    private static string WriteTempYaml(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyphony-version-test-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, content);
        return path;
    }
}
