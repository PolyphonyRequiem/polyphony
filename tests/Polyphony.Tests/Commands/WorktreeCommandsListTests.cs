using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Tests.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

public sealed class WorktreeCommandsListTests : CommandTestBase
{
    private static (WorktreeCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        return (new WorktreeCommands(new GitClient(runner)), runner);
    }

    private static WorktreeListResult Parse(string output) =>
        JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.WorktreeListResult)!;

    // ─── Verb-level tests (full git invocation + emit) ────────────────────

    [Fact]
    public async Task List_InvokesGitWorktreeListPorcelain()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, "", ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.List());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Worktrees.ShouldBeEmpty();
        result.Error.ShouldBeNull();
        runner.Invocations[0].Arguments.ShouldBe(new[] { "worktree", "list", "--porcelain" });
    }

    [Fact]
    public async Task List_SingleWorktree_Parsed()
    {
        var (cmd, runner) = CreateCommand();
        const string porcelain =
            "worktree /repo/main\n" +
            "HEAD abc123def\n" +
            "branch refs/heads/main\n" +
            "\n";
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, porcelain, ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.List());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Worktrees.Count.ShouldBe(1);
        var entry = result.Worktrees[0];
        entry.Path.ShouldBe("/repo/main");
        entry.Head.ShouldBe("abc123def");
        entry.Branch.ShouldBe("main");
        entry.IsBare.ShouldBeFalse();
        entry.IsDetached.ShouldBeFalse();
    }

    [Fact]
    public async Task List_GitFails_RoutesViaErrorField()
    {
        var (cmd, runner) = CreateCommand();
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(128, "", "fatal: not a git repository\n"));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.List());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Worktrees.ShouldBeEmpty();
        result.Error!.ShouldContain("not a git repository");
    }

    [Fact]
    public async Task List_MalformedBlock_RoutesViaErrorField()
    {
        var (cmd, runner) = CreateCommand();
        // Block missing the leading "worktree" key — parser should reject.
        const string porcelain = "HEAD abc123\nbranch refs/heads/main\n\n";
        runner.WhenExact("git", ["worktree", "list", "--porcelain"],
            new ProcessResult(0, porcelain, ""));

        var (exit, output) = await CaptureConsoleAsync(() => cmd.List());

        exit.ShouldBe(ExitCodes.Success);
        var result = Parse(output);
        result.Worktrees.ShouldBeEmpty();
        result.Error!.ShouldContain("missing 'worktree' line");
    }

    // ─── Parser unit tests ────────────────────────────────────────────────

    [Fact]
    public void ParsePorcelain_Empty_ReturnsEmptyList()
    {
        WorktreeCommands.ParsePorcelain("").ShouldBeEmpty();
        WorktreeCommands.ParsePorcelain("\n").ShouldBeEmpty();
        WorktreeCommands.ParsePorcelain("   ").ShouldBeEmpty();
    }

    [Fact]
    public void ParsePorcelain_Multiple_ParsesAllBlocks()
    {
        const string porcelain =
            "worktree /repo/main\n" +
            "HEAD abc123\n" +
            "branch refs/heads/main\n" +
            "\n" +
            "worktree /repo/wt/feature-x\n" +
            "HEAD def456\n" +
            "branch refs/heads/feature/x\n" +
            "\n";

        var entries = WorktreeCommands.ParsePorcelain(porcelain);

        entries.Count.ShouldBe(2);
        entries[0].Path.ShouldBe("/repo/main");
        entries[0].Branch.ShouldBe("main");
        entries[1].Path.ShouldBe("/repo/wt/feature-x");
        entries[1].Branch.ShouldBe("feature/x");
        entries[1].Head.ShouldBe("def456");
    }

    [Fact]
    public void ParsePorcelain_Bare_FlagsIsBareAndLeavesBranchHeadNull()
    {
        const string porcelain =
            "worktree /repo/bare\n" +
            "bare\n" +
            "\n";

        var entries = WorktreeCommands.ParsePorcelain(porcelain);

        entries.Count.ShouldBe(1);
        entries[0].Path.ShouldBe("/repo/bare");
        entries[0].IsBare.ShouldBeTrue();
        entries[0].IsDetached.ShouldBeFalse();
        entries[0].Branch.ShouldBeNull();
        entries[0].Head.ShouldBeNull();
    }

    [Fact]
    public void ParsePorcelain_Detached_FlagsIsDetachedAndLeavesBranchNull()
    {
        const string porcelain =
            "worktree /repo/wt/spike\n" +
            "HEAD 0123abcd\n" +
            "detached\n" +
            "\n";

        var entries = WorktreeCommands.ParsePorcelain(porcelain);

        entries.Count.ShouldBe(1);
        entries[0].IsDetached.ShouldBeTrue();
        entries[0].IsBare.ShouldBeFalse();
        entries[0].Branch.ShouldBeNull();
        entries[0].Head.ShouldBe("0123abcd");
    }

    [Fact]
    public void ParsePorcelain_TrailingBlockWithoutBlankLine_StillParsed()
    {
        // git always terminates with a blank line, but be defensive.
        const string porcelain =
            "worktree /repo/main\n" +
            "HEAD abc123\n" +
            "branch refs/heads/main";

        var entries = WorktreeCommands.ParsePorcelain(porcelain);
        entries.Count.ShouldBe(1);
        entries[0].Branch.ShouldBe("main");
    }

    [Fact]
    public void ParsePorcelain_UnknownKey_Ignored()
    {
        const string porcelain =
            "worktree /repo/main\n" +
            "HEAD abc123\n" +
            "branch refs/heads/main\n" +
            "locked some-reason\n" + // unknown key — ignored, not failed
            "\n";

        var entries = WorktreeCommands.ParsePorcelain(porcelain);
        entries.Count.ShouldBe(1);
        entries[0].Path.ShouldBe("/repo/main");
        entries[0].Branch.ShouldBe("main");
    }

    [Fact]
    public void ParsePorcelain_CrlfLineEndings_Normalised()
    {
        // Windows git emits CRLF on stdout — make sure we don't choke.
        const string porcelain =
            "worktree /repo/main\r\n" +
            "HEAD abc123\r\n" +
            "branch refs/heads/main\r\n" +
            "\r\n";

        var entries = WorktreeCommands.ParsePorcelain(porcelain);
        entries.Count.ShouldBe(1);
        entries[0].Path.ShouldBe("/repo/main");
        entries[0].Head.ShouldBe("abc123");
        entries[0].Branch.ShouldBe("main");
    }

    [Fact]
    public void ParsePorcelain_MissingWorktreeLine_Throws()
    {
        const string porcelain =
            "HEAD abc123\n" +
            "branch refs/heads/main\n" +
            "\n";

        Should.Throw<FormatException>(() => WorktreeCommands.ParsePorcelain(porcelain))
            .Message.ShouldContain("missing 'worktree' line");
    }

    [Fact]
    public void ParsePorcelain_BareAndStandardMix_AllParsed()
    {
        const string porcelain =
            "worktree /repo/bare\n" +
            "bare\n" +
            "\n" +
            "worktree /repo/main\n" +
            "HEAD abc123\n" +
            "branch refs/heads/main\n" +
            "\n" +
            "worktree /repo/wt/spike\n" +
            "HEAD def456\n" +
            "detached\n" +
            "\n";

        var entries = WorktreeCommands.ParsePorcelain(porcelain);

        entries.Count.ShouldBe(3);
        entries[0].IsBare.ShouldBeTrue();
        entries[1].Branch.ShouldBe("main");
        entries[2].IsDetached.ShouldBeTrue();
    }
}
