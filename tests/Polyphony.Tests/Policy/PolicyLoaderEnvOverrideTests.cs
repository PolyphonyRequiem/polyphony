using Polyphony.Policy;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Policy;

/// <summary>
/// Tests for <see cref="PolicyLoader.ResolvePath"/> + <see cref="PolicyLoader.LoadOrDefaultResolved"/> —
/// the env-var override layer added in AB#3103. Precedence: explicit non-default
/// path &gt; <c>POLYPHONY_POLICY_PATH</c> env var &gt; canonical default.
///
/// Each test scrubs the env var before AND after via <see cref="EnvVarScope"/> so
/// xUnit's parallel test runner does not leak state across collections. All file
/// fixtures live in per-test temp directories.
/// </summary>
public sealed class PolicyLoaderEnvOverrideTests
{
    private const string FasttrackYaml = """
        schema_version: 1
        approvals:
          defaults:
            mode: auto
        pr:
          defaults:
            mode: auto
        """;

    private const string ManualYaml = """
        schema_version: 1
        approvals:
          defaults:
            mode: manual
        """;

    [Fact]
    public void ResolvePath_NoEnvNoExplicit_ReturnsDefault()
    {
        using var _ = new EnvVarScope(PolicyLoader.PathEnvVar, null);

        PolicyLoader.ResolvePath(null).ShouldBe(PolicyLoader.DefaultPath);
        PolicyLoader.ResolvePath("").ShouldBe(PolicyLoader.DefaultPath);
        PolicyLoader.ResolvePath(PolicyLoader.DefaultPath).ShouldBe(PolicyLoader.DefaultPath);
    }

    [Fact]
    public void ResolvePath_EnvSet_NoExplicit_ReturnsEnv()
    {
        using var _ = new EnvVarScope(PolicyLoader.PathEnvVar, "/tmp/from-env.yaml");

        PolicyLoader.ResolvePath(null).ShouldBe("/tmp/from-env.yaml");
        PolicyLoader.ResolvePath("").ShouldBe("/tmp/from-env.yaml");
        // The literal default path counts as "no explicit override" so env wins
        PolicyLoader.ResolvePath(PolicyLoader.DefaultPath).ShouldBe("/tmp/from-env.yaml");
    }

    [Fact]
    public void ResolvePath_ExplicitNonDefault_BeatsEnv()
    {
        using var _ = new EnvVarScope(PolicyLoader.PathEnvVar, "/tmp/from-env.yaml");

        PolicyLoader.ResolvePath("/tmp/explicit.yaml").ShouldBe("/tmp/explicit.yaml");
    }

    [Fact]
    public void ResolvePath_EnvWhitespace_TreatedAsUnset()
    {
        using var _ = new EnvVarScope(PolicyLoader.PathEnvVar, "   ");

        PolicyLoader.ResolvePath(null).ShouldBe(PolicyLoader.DefaultPath);
    }

    [Fact]
    public void LoadOrDefaultResolved_EnvPointsToFasttrack_LoadsFasttrack()
    {
        var dir = CreateTempDir();
        try
        {
            var fasttrackPath = Path.Combine(dir, "policy-fasttrack.yaml");
            File.WriteAllText(fasttrackPath, FasttrackYaml);
            using var _ = new EnvVarScope(PolicyLoader.PathEnvVar, fasttrackPath);

            var config = PolicyLoader.LoadOrDefaultResolved(null);

            config.Approvals.ShouldNotBeNull();
            config.Approvals.Defaults.ShouldNotBeNull();
            config.Approvals.Defaults.Mode.ShouldBe(PolicyMode.Auto);
            config.Pr.ShouldNotBeNull();
            config.Pr.Defaults.ShouldNotBeNull();
            config.Pr.Defaults.Mode.ShouldBe(PolicyMode.Auto);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadOrDefaultResolved_ExplicitNonDefault_BeatsEnv()
    {
        var dir = CreateTempDir();
        try
        {
            var fasttrackPath = Path.Combine(dir, "policy-fasttrack.yaml");
            File.WriteAllText(fasttrackPath, FasttrackYaml);
            var manualPath = Path.Combine(dir, "policy-manual.yaml");
            File.WriteAllText(manualPath, ManualYaml);

            using var _ = new EnvVarScope(PolicyLoader.PathEnvVar, fasttrackPath);

            // explicit non-default arg wins over env var
            var config = PolicyLoader.LoadOrDefaultResolved(manualPath);

            config.Approvals!.Defaults!.Mode.ShouldBe(PolicyMode.Manual);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadOrDefaultResolved_EnvPointsToMissingFile_ReturnsDefaultsConfig()
    {
        // Pointing at a non-existent file is the same shape as pointing at a missing
        // canonical default: LoadOrDefault returns a fully-defaulted config.
        using var _ = new EnvVarScope(PolicyLoader.PathEnvVar, "/no/such/file.yaml");

        var config = PolicyLoader.LoadOrDefaultResolved(null);

        config.Approvals!.Defaults!.Mode.ShouldBe(PolicyMode.Warning);
        config.Pr!.Defaults!.Mode.ShouldBe(PolicyMode.Warning);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "polyphony-policy-env-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// RAII scope that sets a process-level env var on construction and restores
    /// the previous value (including unset) on disposal. Lets tests mutate the
    /// env without leaking state to peer tests.
    /// </summary>
    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
            => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
