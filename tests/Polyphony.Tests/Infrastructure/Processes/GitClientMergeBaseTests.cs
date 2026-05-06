using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

/// <summary>
/// Unit tests for <see cref="GitClient.MergeBaseAsync"/> and
/// <see cref="GitClient.IsAncestorAsync"/>. Together these underpin the
/// cascade-remedy verb's "three-fact freshness" check.
/// </summary>
public sealed class GitClientMergeBaseTests
{
    [Fact]
    public async Task MergeBase_Success_ReturnsTrimmedSha()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["merge-base", "HEAD", "origin/main"],
            new ProcessResult(0, "abcdef1234567890\n", ""));
        var client = new GitClient(fake);

        (await client.MergeBaseAsync("HEAD", "origin/main")).ShouldBe("abcdef1234567890");
    }

    [Fact]
    public async Task MergeBase_NoCommonAncestor_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        // git merge-base exits 1 when histories are unrelated.
        fake.WhenExact("git", ["merge-base", "branchA", "branchB"],
            new ProcessResult(1, "", ""));
        var client = new GitClient(fake);

        (await client.MergeBaseAsync("branchA", "branchB")).ShouldBeNull();
    }

    [Fact]
    public async Task MergeBase_BadRef_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["merge-base", "good", "bogus"],
            new ProcessResult(128, "", "fatal: Not a valid object name bogus"));
        var client = new GitClient(fake);

        (await client.MergeBaseAsync("good", "bogus")).ShouldBeNull();
    }

    [Fact]
    public async Task MergeBase_RejectsInvalidArgs()
    {
        var client = new GitClient(new FakeProcessRunner());
        await Should.ThrowAsync<ArgumentException>(async () => await client.MergeBaseAsync("", "b"));
        await Should.ThrowAsync<ArgumentException>(async () => await client.MergeBaseAsync("a", ""));
    }

    [Fact]
    public async Task IsAncestor_Exit0_ReturnsTrue()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["merge-base", "--is-ancestor", "old", "new"],
            new ProcessResult(0, "", ""));
        var client = new GitClient(fake);

        (await client.IsAncestorAsync("old", "new")).ShouldBeTrue();
    }

    [Fact]
    public async Task IsAncestor_Exit1_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["merge-base", "--is-ancestor", "diverged", "head"],
            new ProcessResult(1, "", ""));
        var client = new GitClient(fake);

        (await client.IsAncestorAsync("diverged", "head")).ShouldBeFalse();
    }

    [Fact]
    public async Task IsAncestor_OtherExit_Throws()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["merge-base", "--is-ancestor", "good", "bogus"],
            new ProcessResult(128, "", "fatal: Not a valid commit name bogus"));
        var client = new GitClient(fake);

        var ex = await Should.ThrowAsync<ExternalToolException>(async () =>
            await client.IsAncestorAsync("good", "bogus"));
        ex.ExitCode.ShouldBe(128);
    }

    [Fact]
    public async Task IsAncestor_RejectsInvalidArgs()
    {
        var client = new GitClient(new FakeProcessRunner());
        await Should.ThrowAsync<ArgumentException>(async () => await client.IsAncestorAsync("", "b"));
        await Should.ThrowAsync<ArgumentException>(async () => await client.IsAncestorAsync("a", ""));
    }

    [Fact]
    public async Task MergeBase_PassesArgumentsVerbatim()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("git", ["merge-base", "origin/plan/100", "feature/200"],
            new ProcessResult(0, "shasha\n", ""));
        var client = new GitClient(fake);

        (await client.MergeBaseAsync("origin/plan/100", "feature/200")).ShouldBe("shasha");
        var inv = fake.Invocations.Single();
        inv.Arguments.ShouldBe(["merge-base", "origin/plan/100", "feature/200"]);
    }
}
