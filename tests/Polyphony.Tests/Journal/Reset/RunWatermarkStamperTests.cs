using System.Text.Json.Nodes;
using Polyphony.Infrastructure.Processes;
using Polyphony.Journal.Reset;
using Polyphony.Tagging;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal.Reset;

public sealed class RunWatermarkStamperTests
{
    [Fact]
    public async Task StampAsync_NoPriorWatermark_AddsTagAndPreservesOthers()
    {
        var twig = new FakeTwigClient(initialTags: "polyphony:root; project:cloudvault");
        var stamper = new RunWatermarkStamper(twig);
        var now = new DateTimeOffset(2026, 5, 25, 6, 0, 0, TimeSpan.Zero);

        await stamper.StampAsync(rootId: 100, now, CancellationToken.None);

        var written = twig.LastWrittenTagsField.ShouldNotBeNull();
        var tags = TagSet.Parse(written);
        tags.ShouldContain("polyphony:root");
        tags.ShouldContain("project:cloudvault");
        tags.Count(tag => tag.StartsWith(PolyphonyTags.RunStartedAtPrefix + "=", StringComparison.Ordinal))
            .ShouldBe(1);
        tags.ShouldContain(PolyphonyTags.RunStartedAt(now));
    }

    [Fact]
    public async Task StampAsync_ExistingWatermark_IsReplaced()
    {
        var older = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var twig = new FakeTwigClient(initialTags: $"polyphony:root; {PolyphonyTags.RunStartedAt(older)}");
        var stamper = new RunWatermarkStamper(twig);
        var now = new DateTimeOffset(2026, 5, 25, 6, 0, 0, TimeSpan.Zero);

        await stamper.StampAsync(rootId: 100, now, CancellationToken.None);

        var tags = TagSet.Parse(twig.LastWrittenTagsField);
        tags.ShouldContain("polyphony:root");
        tags.Count(tag => tag.StartsWith(PolyphonyTags.RunStartedAtPrefix + "=", StringComparison.Ordinal))
            .ShouldBe(1);
        tags.ShouldContain(PolyphonyTags.RunStartedAt(now));
        tags.ShouldNotContain(PolyphonyTags.RunStartedAt(older));
    }

    [Fact]
    public async Task StampAsync_DuplicateWatermarks_AllStripped()
    {
        var a = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var b = new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero);
        var twig = new FakeTwigClient(
            initialTags: $"polyphony:root; {PolyphonyTags.RunStartedAt(a)}; {PolyphonyTags.RunStartedAt(b)}");
        var stamper = new RunWatermarkStamper(twig);
        var now = new DateTimeOffset(2026, 5, 25, 6, 0, 0, TimeSpan.Zero);

        await stamper.StampAsync(rootId: 100, now, CancellationToken.None);

        var tags = TagSet.Parse(twig.LastWrittenTagsField);
        tags.Count(tag => tag.StartsWith(PolyphonyTags.RunStartedAtPrefix + "=", StringComparison.Ordinal))
            .ShouldBe(1);
        tags.ShouldContain(PolyphonyTags.RunStartedAt(now));
    }

    [Fact]
    public async Task StampAsync_SyncsBeforeReadAndAfterWrite()
    {
        var twig = new FakeTwigClient(initialTags: "polyphony:root");
        var stamper = new RunWatermarkStamper(twig);

        await stamper.StampAsync(rootId: 100, DateTimeOffset.UtcNow, CancellationToken.None);

        twig.CallLog.ShouldBe(["sync", "show:100", "patch:100", "sync"]);
    }

    [Fact]
    public async Task StampAsync_TwigShowReturnsNull_Throws()
    {
        var twig = new FakeTwigClient(initialTags: null) { ShowReturnsNull = true };
        var stamper = new RunWatermarkStamper(twig);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => stamper.StampAsync(rootId: 100, DateTimeOffset.UtcNow, CancellationToken.None));
        ex.Message.ShouldContain("100");
    }

    private sealed class FakeTwigClient(string? initialTags) : ITwigClient
    {
        public List<string> CallLog { get; } = [];
        public bool ShowReturnsNull { get; set; }
        public string? LastWrittenTagsField { get; private set; }

        private string? _tagsField = initialTags;

        public Task SyncAsync(CancellationToken ct = default)
        {
            CallLog.Add("sync");
            return Task.CompletedTask;
        }

        public Task<JsonNode?> ShowAsync(int workItemId, CancellationToken ct = default)
        {
            CallLog.Add($"show:{workItemId}");
            if (ShowReturnsNull)
            {
                return Task.FromResult<JsonNode?>(null);
            }
            var node = new JsonObject
            {
                ["id"] = workItemId,
                ["tags"] = _tagsField,
            };
            return Task.FromResult<JsonNode?>(node);
        }

        public Task PatchFieldsAsync(int workItemId, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
        {
            CallLog.Add($"patch:{workItemId}");
            if (fields.TryGetValue("System.Tags", out var tags))
            {
                LastWrittenTagsField = tags;
                _tagsField = tags;
            }
            return Task.CompletedTask;
        }

        public Task<string?> GetVersionAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonNode?> ShowTreeAsync(int workItemId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonNode?> TreeAsync(int depth, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetActiveAsync(int workItemId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetStateAsync(string stateName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonNode> CreateChildAsync(int parentId, string type, string title, string description, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetConfigValueAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
