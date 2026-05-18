using Polyphony.Tagging;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Tagging;

/// <summary>
/// Tests for the run-watermark helpers on <see cref="PolyphonyTags"/>:
/// formatter + reader round-trip via <see cref="TagSet"/>. See
/// <c>docs/decisions/run-reset.md</c> for the design rationale.
/// </summary>
public sealed class PolyphonyTagsRunStartedAtTests
{
    [Fact]
    public void RunStartedAt_FormatsAsIsoUtcWithMillisecondPrecision()
    {
        var instant = new DateTimeOffset(2026, 5, 17, 22, 30, 45, 123, TimeSpan.Zero);

        var tag = PolyphonyTags.RunStartedAt(instant);

        tag.ShouldBe("polyphony:run-started-at=2026-05-17T22:30:45.123Z");
    }

    [Fact]
    public void RunStartedAt_FormatsZeroMillisecondsExplicitly()
    {
        // ISO `.fff` always emits three digits — keeps the tag shape
        // stable and round-trip-clean regardless of the input precision.
        var instant = new DateTimeOffset(2026, 5, 17, 22, 30, 45, TimeSpan.Zero);

        var tag = PolyphonyTags.RunStartedAt(instant);

        tag.ShouldBe("polyphony:run-started-at=2026-05-17T22:30:45.000Z");
    }

    [Fact]
    public void RunStartedAt_NormalisesNonUtcOffsetsToUtc()
    {
        // 2026-05-17 22:30 +05:00 == 2026-05-17 17:30 UTC
        var instant = new DateTimeOffset(2026, 5, 17, 22, 30, 0, TimeSpan.FromHours(5));

        var tag = PolyphonyTags.RunStartedAt(instant);

        tag.ShouldBe("polyphony:run-started-at=2026-05-17T17:30:00.000Z");
    }

    [Fact]
    public void ReadRunStartedAt_RoundTripsViaTagSet()
    {
        var instant = new DateTimeOffset(2026, 5, 17, 22, 30, 45, 678, TimeSpan.Zero);
        var tags = TagSet.Parse($"polyphony; polyphony:root; {PolyphonyTags.RunStartedAt(instant)}");

        var parsed = PolyphonyTags.ReadRunStartedAt(tags);

        parsed.ShouldNotBeNull();
        parsed!.Value.UtcDateTime.ShouldBe(instant.UtcDateTime);
    }

    [Fact]
    public void ReadRunStartedAt_TagAbsent_ReturnsNull()
    {
        var tags = TagSet.Parse("polyphony; polyphony:root; polyphony:planned");

        PolyphonyTags.ReadRunStartedAt(tags).ShouldBeNull();
    }

    [Fact]
    public void ReadRunStartedAt_EmptyTagSet_ReturnsNull()
    {
        PolyphonyTags.ReadRunStartedAt(TagSet.Empty).ShouldBeNull();
    }

    [Fact]
    public void ReadRunStartedAt_MalformedValue_ReturnsNull()
    {
        // Reader posture: null = "no filter". Garbage values must NOT be
        // treated as epoch zero (which would filter EVERY merged PR).
        var tags = TagSet.Parse("polyphony:run-started-at=not-a-date");

        PolyphonyTags.ReadRunStartedAt(tags).ShouldBeNull();
    }

    [Fact]
    public void ReadRunStartedAt_PrefixWithoutEquals_Ignored()
    {
        // Bare prefix (no `=value`) is not a malformed run-started-at; the
        // reader should skip it and return null.
        var tags = TagSet.Parse("polyphony:run-started-at");

        PolyphonyTags.ReadRunStartedAt(tags).ShouldBeNull();
    }

    [Fact]
    public void ReadRunStartedAt_DuplicateTags_ReturnsMaxValuedParseable()
    {
        // Defensive against PR 2 reset bugs and manual operator edits
        // that leave two prefix tags coexisting. TagSet dedupes by exact
        // string; two run-started-at values are distinct strings, so
        // both survive Parse. The reader MUST scan all matches and pick
        // the latest — picking the first would silently fall back to an
        // earlier watermark and re-introduce the lying-merged bug.
        var earlier = new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        var tags = TagSet.Parse(
            $"{PolyphonyTags.RunStartedAt(earlier)}; {PolyphonyTags.RunStartedAt(later)}");

        var parsed = PolyphonyTags.ReadRunStartedAt(tags);

        parsed.ShouldNotBeNull();
        parsed!.Value.UtcDateTime.ShouldBe(later.UtcDateTime);
    }

    [Fact]
    public void ReadRunStartedAt_DuplicateTagsWithMalformedFirst_ReturnsValidValue()
    {
        // First match is unparseable; the reader must skip it and
        // continue scanning rather than returning null on first miss.
        var valid = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        var tags = TagSet.Parse(
            $"polyphony:run-started-at=not-a-date; {PolyphonyTags.RunStartedAt(valid)}");

        var parsed = PolyphonyTags.ReadRunStartedAt(tags);

        parsed.ShouldNotBeNull();
        parsed!.Value.UtcDateTime.ShouldBe(valid.UtcDateTime);
    }

    [Theory]
    [InlineData("2026-05-17T22:30:45Z")]
    [InlineData("2026-05-17T22:30:45.000Z")]
    [InlineData("2026-05-17T22:30:45+00:00")]
    [InlineData("2026-05-17T17:30:45-05:00")] // == 22:30:45Z
    public void ReadRunStartedAt_AcceptsCommonIsoForms_NormalisesToUtc(string value)
    {
        var tags = TagSet.Parse($"polyphony:run-started-at={value}");

        var parsed = PolyphonyTags.ReadRunStartedAt(tags);

        parsed.ShouldNotBeNull();
        parsed!.Value.UtcDateTime.ShouldBe(new DateTime(2026, 5, 17, 22, 30, 45, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadRunStartedAt_SubsecondRoundTrip_PreservedToMillisecond()
    {
        // The whole point of the precision bump: a PR merging in the
        // same second as reset must be distinguishable. Round-trip a
        // sub-second instant and confirm it survives.
        var instant = new DateTimeOffset(2026, 5, 17, 12, 0, 0, 950, TimeSpan.Zero);
        var tags = TagSet.Parse(PolyphonyTags.RunStartedAt(instant));

        var parsed = PolyphonyTags.ReadRunStartedAt(tags);

        parsed.ShouldNotBeNull();
        parsed!.Value.UtcDateTime.ShouldBe(instant.UtcDateTime);
        parsed.Value.Millisecond.ShouldBe(950);
    }
}
