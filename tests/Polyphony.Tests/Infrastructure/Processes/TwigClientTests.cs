using System.Text.Json.Nodes;
using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

public sealed class TwigClientTests
{
    [Fact]
    public async Task GetVersionAsync_Success_ReturnsTrimmedString()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["--version"], new ProcessResult(0, "twig 1.2.3\n", ""));
        var client = new TwigClient(fake);

        var version = await client.GetVersionAsync();

        version.ShouldBe("twig 1.2.3");
    }

    [Fact]
    public async Task GetVersionAsync_NonZeroExit_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["--version"], new ProcessResult(1, "", "boom"));
        var client = new TwigClient(fake);

        (await client.GetVersionAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task SyncAsync_Success_DiscardsStdout()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(0, "{\"synced\":true}", ""));
        var client = new TwigClient(fake);

        await client.SyncAsync(); // should not throw
    }

    [Fact]
    public async Task SyncAsync_Failure_ThrowsExternalToolException()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["sync", "--output", "json"], new ProcessResult(1, "", "ado unreachable"));
        var client = new TwigClient(fake);

        var ex = await Should.ThrowAsync<ExternalToolException>(async () => await client.SyncAsync());
        ex.Executable.ShouldBe("twig");
        ex.ExitCode.ShouldBe(1);
        ex.Stderr.ShouldContain("ado unreachable");
    }

    [Fact]
    public async Task ShowAsync_Success_ReturnsParsedJson()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["show", "42", "--output", "json"],
            new ProcessResult(0, """{"id":42,"title":"foo"}""", ""));
        var client = new TwigClient(fake);

        var node = await client.ShowAsync(42);

        node.ShouldNotBeNull();
        node["id"]!.GetValue<int>().ShouldBe(42);
        node["title"]!.GetValue<string>().ShouldBe("foo");
    }

    [Fact]
    public async Task ShowAsync_NonZeroExit_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["show", "999", "--output", "json"],
            new ProcessResult(1, "", "not found"));
        var client = new TwigClient(fake);

        (await client.ShowAsync(999)).ShouldBeNull();
    }

    [Fact]
    public async Task ShowTreeAsync_Success_ReturnsParsedJsonWithChildren()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["show", "1", "--tree", "--output", "json"],
            new ProcessResult(0, """{"id":1,"children":[{"id":2}]}""", ""));
        var client = new TwigClient(fake);

        var node = await client.ShowTreeAsync(1);

        node.ShouldNotBeNull();
        node["children"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task TreeAsync_BuildsCorrectArgs()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["tree", "--depth", "3", "--output", "json"],
            new ProcessResult(0, """{"id":1}""", ""));
        var client = new TwigClient(fake);

        var node = await client.TreeAsync(3);

        node.ShouldNotBeNull();
        fake.Invocations[0].Arguments.ShouldBe(["tree", "--depth", "3", "--output", "json"]);
    }

    [Fact]
    public async Task SetActiveAsync_Success_DoesNotThrow()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["set", "100", "--output", "json"], new ProcessResult(0, "{}", ""));
        var client = new TwigClient(fake);

        await client.SetActiveAsync(100);
    }

    [Fact]
    public async Task SetStateAsync_PassesStateName()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["state", "Done", "--output", "json"], new ProcessResult(0, "{}", ""));
        var client = new TwigClient(fake);

        await client.SetStateAsync("Done");

        fake.Invocations[0].Arguments.ShouldBe(["state", "Done", "--output", "json"]);
    }

    [Fact]
    public async Task PatchFieldsAsync_SerializesFieldsAsJson()
    {
        var fake = new FakeProcessRunner();
        fake.When(
            (exe, args) =>
                exe == "twig"
                && args.Count >= 4
                && args[0] == "patch"
                && args[1] == "--id"
                && args[2] == "55"
                && args[3] == "--json",
            new ProcessResult(0, "{}", ""));
        var client = new TwigClient(fake);

        var fields = new Dictionary<string, string>
        {
            ["System.Title"] = "New title",
            ["System.Tags"] = "polyphony:planned",
        };
        await client.PatchFieldsAsync(55, fields);

        var sent = fake.Invocations[0];
        sent.Arguments.Count.ShouldBe(5);
        var json = sent.Arguments[4];
        // Round-trip via JsonNode to assert content semantically.
        var parsed = JsonNode.Parse(json)!;
        parsed["System.Title"]!.GetValue<string>().ShouldBe("New title");
        parsed["System.Tags"]!.GetValue<string>().ShouldBe("polyphony:planned");
    }

    [Fact]
    public async Task CreateChildAsync_Success_ReturnsParsedJsonNode()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig",
            ["new", "--type", "Task", "--title", "T", "--description", "D", "--parent", "1", "-o", "json"],
            new ProcessResult(0, """{"id":42,"title":"T"}""", ""));
        var client = new TwigClient(fake);

        var node = await client.CreateChildAsync(1, "Task", "T", "D");

        node["id"]!.GetValue<int>().ShouldBe(42);
    }

    [Fact]
    public async Task CreateChildAsync_NonZeroExit_ThrowsExternalToolException()
    {
        var fake = new FakeProcessRunner();
        fake.WhenStartsWith("twig", ["new"], new ProcessResult(1, "", "type 'Task' not allowed under 'Task'"));
        var client = new TwigClient(fake);

        await Should.ThrowAsync<ExternalToolException>(async () =>
            await client.CreateChildAsync(1, "Task", "T", "D"));
    }

    [Fact]
    public async Task GetConfigValueAsync_Success_UnwrapsInfoField()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["config", "organization", "--output", "json"],
            new ProcessResult(0, """{"info":"my-org"}""", ""));
        var client = new TwigClient(fake);

        var value = await client.GetConfigValueAsync("organization");

        value.ShouldBe("my-org");
    }

    [Fact]
    public async Task GetConfigValueAsync_FailedCall_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["config", "missing", "--output", "json"],
            new ProcessResult(1, "", "key missing"));
        var client = new TwigClient(fake);

        (await client.GetConfigValueAsync("missing")).ShouldBeNull();
    }

    [Fact]
    public async Task GetConfigValueAsync_MalformedJson_ReturnsNull()
    {
        var fake = new FakeProcessRunner();
        fake.WhenExact("twig", ["config", "weird", "--output", "json"],
            new ProcessResult(0, "not json", ""));
        var client = new TwigClient(fake);

        (await client.GetConfigValueAsync("weird")).ShouldBeNull();
    }
}
