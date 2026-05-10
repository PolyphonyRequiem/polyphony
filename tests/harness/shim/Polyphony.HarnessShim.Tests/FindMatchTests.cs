using Polyphony.HarnessShim;
using Shouldly;
using Xunit;

namespace Polyphony.HarnessShim.Tests;

public sealed class FindMatchTests
{
    [Fact]
    public void ExactCommandAndPrefixArgs_Matches()
    {
        var responses = new List<ManifestResponse>
        {
            new() { Command = "polyphony", Args = ["plan", "classify-stale-descendants"], Stdout = "ok", ExitCode = 0 },
        };

        var match = Program.FindMatch(responses, "polyphony", ["plan", "classify-stale-descendants", "--root-id", "42"]);

        match.ShouldNotBeNull();
        match!.Stdout.ShouldBe("ok");
    }

    [Fact]
    public void DifferentCommand_DoesNotMatch()
    {
        var responses = new List<ManifestResponse>
        {
            new() { Command = "polyphony", Args = ["plan"], ExitCode = 0 },
        };

        var match = Program.FindMatch(responses, "twig", ["plan"]);

        match.ShouldBeNull();
    }

    [Fact]
    public void EntryArgsLongerThanInvocation_DoesNotMatch()
    {
        var responses = new List<ManifestResponse>
        {
            new() { Command = "polyphony", Args = ["plan", "classify-stale-descendants"], ExitCode = 0 },
        };

        var match = Program.FindMatch(responses, "polyphony", ["plan"]);

        match.ShouldBeNull();
    }

    [Fact]
    public void FirstMatchWins_BySpecificityOrder()
    {
        // Authors put the most specific entry first.
        var responses = new List<ManifestResponse>
        {
            new() { Command = "polyphony", Args = ["plan", "classify-stale-descendants"], Stdout = "specific", ExitCode = 0 },
            new() { Command = "polyphony", Args = ["plan"], Stdout = "general", ExitCode = 0 },
        };

        var match = Program.FindMatch(responses, "polyphony", ["plan", "classify-stale-descendants", "--root-id", "42"]);

        match.ShouldNotBeNull();
        match!.Stdout.ShouldBe("specific");
    }

    [Fact]
    public void EmptyArgsEntry_MatchesAnyInvocationOfThatCommand()
    {
        // Useful as a catch-all when authors don't care which subverb is invoked.
        var responses = new List<ManifestResponse>
        {
            new() { Command = "gh", Args = [], Stdout = "stub", ExitCode = 0 },
        };

        var match = Program.FindMatch(responses, "gh", ["pr", "view", "42", "--json", "state"]);

        match.ShouldNotBeNull();
        match!.Stdout.ShouldBe("stub");
    }

    [Fact]
    public void NoEntries_ReturnsNull()
    {
        var match = Program.FindMatch(Array.Empty<ManifestResponse>(), "polyphony", ["plan"]);

        match.ShouldBeNull();
    }
}
