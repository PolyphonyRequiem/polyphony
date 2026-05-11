using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

public sealed class GitClientTests
{
    [Fact]
    public async Task GetTopLevelAsync_Success_ReturnsTrimmedPath()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["rev-parse", "--show-toplevel"],
            new ProcessResult(0, "/Users/dan/projects/polyphony\n", ""));
        var client = new GitClient(fake);

        (await client.GetTopLevelAsync()).ShouldBe("/Users/dan/projects/polyphony");
    }

    [Fact]
    public async Task GetTopLevelAsync_NotInRepo_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["rev-parse", "--show-toplevel"],
            new ProcessResult(128, "", "fatal: not a git repository"));
        var client = new GitClient(fake);

        (await client.GetTopLevelAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task GetCommonDirAsync_MainWorktree_ReturnsAbsoluteGitDir()
    {
        var fake = new FakeProcessRunner();
        // From the main worktree, common dir is the repo's .git directory
        // (absolute form because we passed --path-format=absolute).
        fake.WhenExact("git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "/Users/dan/projects/polyphony/.git\n", ""));
        var client = new GitClient(fake);

        (await client.GetCommonDirAsync()).ShouldBe("/Users/dan/projects/polyphony/.git");
    }

    [Fact]
    public async Task GetCommonDirAsync_LinkedWorktree_ReturnsMainWorktreeGitDir()
    {
        var fake = new FakeProcessRunner();
        // From a linked worktree at `../polyphony-3067`, --git-common-dir
        // still returns the main repo's .git (NOT
        // `../polyphony/.git/worktrees/polyphony-3067`). This is the
        // convergence guarantee that makes cross-worktree shared state work.
        fake.WhenExact("git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "/Users/dan/projects/polyphony/.git\n", ""));
        var client = new GitClient(fake);

        (await client.GetCommonDirAsync()).ShouldBe("/Users/dan/projects/polyphony/.git");
    }

    [Fact]
    public async Task GetCommonDirAsync_NotInRepo_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(128, "", "fatal: not a git repository"));
        var client = new GitClient(fake);

        (await client.GetCommonDirAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task GetCommonDirAsync_TrimsTrailingNewline()
    {
        // git emits a trailing newline; consumers build paths with
        // Path.Combine and would silently produce path strings with
        // an embedded newline if we didn't trim.
        var fake = new FakeProcessRunner();
        fake.WhenExact("git",
            ["rev-parse", "--path-format=absolute", "--git-common-dir"],
            new ProcessResult(0, "C:\\Users\\dan\\polyphony\\.git\r\n", ""));
        var client = new GitClient(fake);

        (await client.GetCommonDirAsync()).ShouldBe("C:\\Users\\dan\\polyphony\\.git");
    }

    [Fact]
    public async Task GetCurrentBranchAsync_Success_ReturnsBranchName()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["branch", "--show-current"],
            new ProcessResult(0, "sdlc/2978\n", ""));
        var client = new GitClient(fake);

        (await client.GetCurrentBranchAsync()).ShouldBe("sdlc/2978");
    }

    [Fact]
    public async Task GetCurrentBranchAsync_DetachedHead_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        // git branch --show-current emits empty stdout when detached.
        fake.WhenExact("git", ["branch", "--show-current"], new ProcessResult(0, "\n", ""));
        var client = new GitClient(fake);

        (await client.GetCurrentBranchAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task GetRemoteUrlAsync_DefaultsToOrigin()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["remote", "get-url", "origin"],
            new ProcessResult(0, "git@github.com:owner/repo.git\n", ""));
        var client = new GitClient(fake);

        (await client.GetRemoteUrlAsync()).ShouldBe("git@github.com:owner/repo.git");
    }

    [Fact]
    public async Task GetRemoteUrlAsync_CustomRemote_PassesThrough()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["remote", "get-url", "upstream"],
            new ProcessResult(0, "https://github.com/upstream/repo.git\n", ""));
        var client = new GitClient(fake);

        (await client.GetRemoteUrlAsync("upstream"))
            .ShouldBe("https://github.com/upstream/repo.git");
    }

    [Fact]
    public async Task ListRemoteBranchesAsync_StripsOriginPrefixAndSkipsSymlink()
    {
        var fake = new FakeProcessRunner();
        var stdout = """
              origin/HEAD -> origin/main
              origin/main
              origin/feature/2978-foo
              origin/sdlc/2978
            """;
        fake.WhenExact("git", ["branch", "-r"], new ProcessResult(0, stdout, ""));
        var client = new GitClient(fake);

        var branches = await client.ListRemoteBranchesAsync();

        branches.ShouldBe(["main", "feature/2978-foo", "sdlc/2978"]);
    }

    [Fact]
    public async Task ListRemoteBranchesAsync_FailedCall_ReturnsEmpty()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["branch", "-r"], new ProcessResult(1, "", "not a repo"));
        var client = new GitClient(fake);

        (await client.ListRemoteBranchesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task LsRemoteHeadsAsync_Success_ReturnsLines()
    {
        var fake = new FakeProcessRunner();
        var stdout = "abc123\trefs/heads/feature/2978-pg-1\ndef456\trefs/heads/feature/2978-pg-2\n";
        fake.WhenExact("git", ["ls-remote", "--heads", "origin", "feature/2978-*"],
            new ProcessResult(0, stdout, ""));
        var client = new GitClient(fake);

        var lines = await client.LsRemoteHeadsAsync("origin", "feature/2978-*");

        lines.Count.ShouldBe(2);
        lines[0].ShouldContain("feature/2978-pg-1");
    }

    [Fact]
    public async Task LsRemoteHeadsAsync_NoMatches_ReturnsEmpty()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["ls-remote", "--heads", "origin", "no-such-*"],
            new ProcessResult(0, "", ""));
        var client = new GitClient(fake);

        (await client.LsRemoteHeadsAsync("origin", "no-such-*")).ShouldBeEmpty();
    }

    [Fact]
    public async Task WorktreeAddAttachAsync_Success_PassesPathThenBranchWithoutDashB()
    {
        // Argument order is `git worktree add <path> <branch>` — NOT
        // `<branch> <path>`, NOT `-b <branch> <path>`. Inverting either
        // would silently change git's semantics (attach vs create).
        var fake = new FakeProcessRunner();
        fake.WhenExact("git",
            ["worktree", "add", "/runs/apex-3085/impl-3085-3072", "impl/3085-3072"],
            new ProcessResult(0, "Preparing worktree...\n", ""));
        var client = new GitClient(fake);

        var result = await client.WorktreeAddAttachAsync(
            branch: "impl/3085-3072",
            path: "/runs/apex-3085/impl-3085-3072");

        result.Succeeded.ShouldBeTrue();
        var invocation = fake.Invocations.ShouldHaveSingleItem();
        invocation.Arguments.ShouldNotContain("-b");
    }

    [Fact]
    public async Task WorktreeAddAttachAsync_GitFails_ReturnsResultWithoutThrowing()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git",
            ["worktree", "add", "/runs/apex-3085/impl-3085-3072", "impl/3085-3072"],
            new ProcessResult(128, "", "fatal: 'impl/3085-3072' is already checked out at '/elsewhere'\n"));
        var client = new GitClient(fake);

        var result = await client.WorktreeAddAttachAsync("impl/3085-3072", "/runs/apex-3085/impl-3085-3072");

        result.Succeeded.ShouldBeFalse();
        result.Stderr.ShouldContain("already checked out");
    }

    [Fact]
    public async Task WorktreeAddAttachAsync_EmptyBranchOrPath_Throws()
    {
        var client = new GitClient(new FakeProcessRunner());
        await Should.ThrowAsync<ArgumentException>(() => client.WorktreeAddAttachAsync("", "/p"));
        await Should.ThrowAsync<ArgumentException>(() => client.WorktreeAddAttachAsync("b", ""));
    }
}
