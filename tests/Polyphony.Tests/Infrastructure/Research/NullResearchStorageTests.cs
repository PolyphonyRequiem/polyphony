using Polyphony.Infrastructure.Research;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Research;

public sealed class NullResearchStorageTests
{
    private readonly NullResearchStorage _storage = new();

    [Fact]
    public async Task ReadAsync_ReturnsNull()
    {
        var result = await _storage.ReadAsync("any/path.md");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_ReturnsEmpty()
    {
        var result = await _storage.ListAsync("any/dir");

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteAsync_NoOpsAndDoesNotThrow()
    {
        await _storage.WriteAsync("path.md", "content", "msg");
    }

    [Fact]
    public async Task WriteAsync_OnlyWarnsOnceAcrossManyWrites()
    {
        var originalErr = Console.Error;
        try
        {
            using var capture = new StringWriter();
            Console.SetError(capture);

            for (var i = 0; i < 5; i++)
            {
                await _storage.WriteAsync($"path-{i}.md", "content", "msg");
            }

            var output = capture.ToString();
            var occurrences = output.Split("research storage is not configured").Length - 1;
            occurrences.ShouldBe(1);
            output.ShouldContain("path-0.md");
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
