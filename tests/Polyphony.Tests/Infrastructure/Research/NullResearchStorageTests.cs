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
    public void WriteAsync_ThrowsInvalidOperationException()
    {
        Should.Throw<InvalidOperationException>(
            () => _storage.WriteAsync("path.md", "content", "msg"));
    }
}
