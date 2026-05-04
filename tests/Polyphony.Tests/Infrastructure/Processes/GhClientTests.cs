using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

public sealed class GhClientTests
{
    [Fact]
    public async Task GetAuthStatusAsync_Authenticated_ReturnsTrueWithStderrDetail()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("gh", ["auth", "status"],
            new ProcessResult(0, "", "✓ Logged in to github.com as user"));
        var client = new GhClient(fake);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeTrue();
        status.Detail.ShouldContain("Logged in");
    }

    [Fact]
    public async Task GetAuthStatusAsync_NotAuthenticated_ReturnsFalse()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("gh", ["auth", "status"],
            new ProcessResult(1, "", "You are not logged into any GitHub hosts"));
        var client = new GhClient(fake);

        var status = await client.GetAuthStatusAsync();

        status.IsAuthenticated.ShouldBeFalse();
        status.Detail.ShouldContain("not logged in");
    }

    [Fact]
    public async Task ListPullRequestsAsync_Success_ParsesAllFields()
    {
        var fake = new FakeProcessRunner();
        var stdout = """
            [
              {"number":42,"headRefName":"feature/2978-pg-1","url":"https://github.com/o/r/pull/42","mergedAt":"2026-05-04T10:00:00Z"},
              {"number":43,"headRefName":"feature/2978-pg-2","url":null,"mergedAt":null}
            ]
            """;
        fake.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, stdout, ""));
        var client = new GhClient(fake);

        var prs = await client.ListPullRequestsAsync("o/r", new PrListFilters(State: "merged", Limit: 50));

        prs.Count.ShouldBe(2);
        prs[0].Number.ShouldBe(42);
        prs[0].HeadRefName.ShouldBe("feature/2978-pg-1");
        prs[0].Url.ShouldBe("https://github.com/o/r/pull/42");
        prs[0].MergedAt.ShouldNotBeNull();
        prs[1].Url.ShouldBeNull();
        prs[1].MergedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ListPullRequestsAsync_AppendsAllSuppliedFilters()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "[]", ""));
        var client = new GhClient(fake);

        await client.ListPullRequestsAsync("o/r",
            new PrListFilters(Head: "feature/x", Base: "main", State: "open", Limit: 10));

        var args = fake.Invocations[0].Arguments;
        args.ShouldContain("--repo");
        args.ShouldContain("o/r");
        args.ShouldContain("--head");
        args.ShouldContain("feature/x");
        args.ShouldContain("--base");
        args.ShouldContain("main");
        args.ShouldContain("--state");
        args.ShouldContain("open");
        args.ShouldContain("--limit");
        args.ShouldContain("10");
        args.ShouldContain("--json");
        args.ShouldContain("number,headRefName,url,mergedAt");
    }

    [Fact]
    public async Task ListPullRequestsAsync_FailedCall_ReturnsEmpty()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "list"],
            new ProcessResult(1, "", "could not authenticate"));
        var client = new GhClient(fake);

        (await client.ListPullRequestsAsync("o/r", new PrListFilters())).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListPullRequestsAsync_MalformedJson_ReturnsEmpty()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "list"], new ProcessResult(0, "not-json", ""));
        var client = new GhClient(fake);

        (await client.ListPullRequestsAsync("o/r", new PrListFilters())).ShouldBeEmpty();
    }

    [Fact]
    public async Task CreatePullRequestAsync_Success_ReturnsTrimmedUrl()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "create"],
            new ProcessResult(0, "https://github.com/o/r/pull/100\n", ""));
        var client = new GhClient(fake);

        var url = await client.CreatePullRequestAsync("o/r", "main", "feature/x", "title", "body");

        url.ShouldBe("https://github.com/o/r/pull/100");
    }

    [Fact]
    public async Task CreatePullRequestAsync_PassesAllArgs()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "create"],
            new ProcessResult(0, "https://github.com/o/r/pull/1", ""));
        var client = new GhClient(fake);

        await client.CreatePullRequestAsync("o/r", "main", "feature/x", "feat: title", "## Body");

        var args = fake.Invocations[0].Arguments;
        args.ShouldBe([
            "pr", "create",
            "--repo", "o/r",
            "--base", "main",
            "--head", "feature/x",
            "--title", "feat: title",
            "--body", "## Body",
        ]);
    }

    [Fact]
    public async Task CreatePullRequestAsync_NonZeroExit_ThrowsExternalToolException()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "create"],
            new ProcessResult(1, "", "head branch does not exist"));
        var client = new GhClient(fake);

        var ex = await Should.ThrowAsync<ExternalToolException>(async () =>
            await client.CreatePullRequestAsync("o/r", "main", "feature/x", "t", "b"));
        ex.ExitCode.ShouldBe(1);
        ex.Stderr.ShouldContain("head branch does not exist");
    }

    [Fact]
    public async Task CreatePullRequestAsync_EmptyStdout_ThrowsExternalToolException()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("gh", ["pr", "create"],
            new ProcessResult(0, "", ""));
        var client = new GhClient(fake);

        await Should.ThrowAsync<ExternalToolException>(async () =>
            await client.CreatePullRequestAsync("o/r", "main", "feature/x", "t", "b"));
    }
}
