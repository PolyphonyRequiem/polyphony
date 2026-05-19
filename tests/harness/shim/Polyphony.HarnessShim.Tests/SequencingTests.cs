using Polyphony.HarnessShim;
using Shouldly;
using Xunit;

namespace Polyphony.HarnessShim.Tests;

/// <summary>
/// Sequencing tests for <see cref="Program.FindMatchWithIndex"/>: per-entry
/// <c>times</c> caps, fall-through to later duplicate matchers, and counter
/// state load/save round-tripping through the on-disk JSON store.
/// </summary>
public sealed class SequencingTests
{
    [Fact]
    public void UnlimitedTimes_BehavesLikeLegacyFirstMatchWins()
    {
        // No times set on either entry → first entry wins on every call,
        // regardless of how many times we pretend it's been called.
        var responses = new List<ManifestResponse>
        {
            new() { Command = "polyphony", Args = ["pr", "poll-status"], Stdout = "first", ExitCode = 0 },
            new() { Command = "polyphony", Args = ["pr", "poll-status"], Stdout = "second", ExitCode = 0 },
        };

        var (match, index) = Program.FindMatchWithIndex(
            responses,
            "polyphony",
            ["pr", "poll-status", "--pr-url", "https://x"],
            counters: new Dictionary<string, int> { ["0"] = 999 });

        match.ShouldNotBeNull();
        match!.Stdout.ShouldBe("first");
        index.ShouldBe(0);
    }

    [Fact]
    public void ExhaustedTimesEntry_FallsThroughToNextDuplicateMatcher()
    {
        // Sequencing pattern: same (command, args) listed twice, each with
        // times: 1. After the first selection consumes entry 0, the next
        // match should land on entry 1.
        var responses = new List<ManifestResponse>
        {
            new() { Command = "polyphony", Args = ["pr", "poll-status"], Stdout = "none", ExitCode = 0, Times = 1 },
            new() { Command = "polyphony", Args = ["pr", "poll-status"], Stdout = "merge_now", ExitCode = 0, Times = 1 },
        };

        // Simulate entry 0 already consumed.
        var counters = new Dictionary<string, int> { ["0"] = 1 };

        var (match, index) = Program.FindMatchWithIndex(
            responses, "polyphony", ["pr", "poll-status"], counters);

        match.ShouldNotBeNull();
        match!.Stdout.ShouldBe("merge_now");
        index.ShouldBe(1);
    }

    [Fact]
    public void AllFiniteMatchersExhausted_ReturnsNoMatch()
    {
        var responses = new List<ManifestResponse>
        {
            new() { Command = "polyphony", Args = ["pr", "poll-status"], Stdout = "a", ExitCode = 0, Times = 1 },
            new() { Command = "polyphony", Args = ["pr", "poll-status"], Stdout = "b", ExitCode = 0, Times = 1 },
        };

        var counters = new Dictionary<string, int> { ["0"] = 1, ["1"] = 1 };

        var (match, index) = Program.FindMatchWithIndex(
            responses, "polyphony", ["pr", "poll-status"], counters);

        match.ShouldBeNull();
        index.ShouldBe(-1);
    }

    [Fact]
    public void ExhaustedFiniteMatcher_FallsThroughToUnlimitedFallback()
    {
        // Mixed sequencing + unlimited fallback: first call burns the
        // finite entry, all subsequent calls land on the unlimited one.
        var responses = new List<ManifestResponse>
        {
            new() { Command = "polyphony", Args = ["pr", "poll-status"], Stdout = "first_only", ExitCode = 0, Times = 1 },
            new() { Command = "polyphony", Args = ["pr", "poll-status"], Stdout = "forever_after", ExitCode = 0 },
        };

        var counters = new Dictionary<string, int> { ["0"] = 1 };

        var (match, index) = Program.FindMatchWithIndex(
            responses, "polyphony", ["pr", "poll-status"], counters);

        match.ShouldNotBeNull();
        match!.Stdout.ShouldBe("forever_after");
        index.ShouldBe(1);
    }

    [Fact]
    public void FindMatchLegacy_StillReturnsFirstMatchIgnoringTimesField()
    {
        // The 3-arg FindMatch overload is what existing tests use. With Times
        // set but no counters supplied, the legacy entry should match on
        // call #1 because the implicit counters dictionary is empty.
        var responses = new List<ManifestResponse>
        {
            new() { Command = "polyphony", Args = ["plan"], Stdout = "first", ExitCode = 0, Times = 1 },
            new() { Command = "polyphony", Args = ["plan"], Stdout = "second", ExitCode = 0 },
        };

        var match = Program.FindMatch(responses, "polyphony", ["plan", "classify"]);

        match.ShouldNotBeNull();
        match!.Stdout.ShouldBe("first");
    }

    [Fact]
    public void SaveAndLoadCounters_RoundTripsValues()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"harness-shim-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var counterPath = Path.Combine(tempDir, "manifest.json.counters.json");
            var input = new Dictionary<string, int> { ["0"] = 3, ["2"] = 1 };

            Program.SaveCounters(counterPath, input);
            var loaded = Program.LoadCounters(counterPath);

            loaded["0"].ShouldBe(3);
            loaded["2"].ShouldBe(1);
            loaded.ContainsKey("1").ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadCounters_ReturnsEmpty_WhenFileMissing()
    {
        var counterPath = Path.Combine(
            Path.GetTempPath(),
            $"harness-shim-test-missing-{Guid.NewGuid():N}.json");

        var loaded = Program.LoadCounters(counterPath);

        loaded.ShouldNotBeNull();
        loaded.ShouldBeEmpty();
    }

    [Fact]
    public void LoadCounters_TolerantOfMalformedFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"harness-shim-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var counterPath = Path.Combine(tempDir, "counters.json");
            File.WriteAllText(counterPath, "not json at all");

            var loaded = Program.LoadCounters(counterPath);

            loaded.ShouldNotBeNull();
            loaded.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CounterPathForManifest_AppendsCountersSuffix()
    {
        // Sibling of the manifest, deterministic so multi-invocation shim
        // calls can find each other's counter state.
        var manifest = Path.Combine("/tmp", "scenario-bin", "manifest.json");

        var counterPath = Program.CounterPathForManifest(manifest);

        counterPath.ShouldBe(manifest + ".counters.json");
    }
}
