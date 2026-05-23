using Polyphony.Journal;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Journal;

/// <summary>
/// W11 (AB#3292): the <c>journal_lineages</c> table plus the
/// <see cref="JournalStore.RecordLineageAsync"/> /
/// <see cref="JournalStore.GetLineagesAsync"/> /
/// <see cref="JournalStore.RetireLineageAsync"/> trio that anchor
/// W12 (reset retire), W13 (reconcile), and W14 (cross-machine attach).
/// </summary>
public sealed class JournalLineageStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JournalStore _store;

    public JournalLineageStoreTests()
    {
        SQLitePCL.Batteries.Init();
        _tempDir = Path.Combine(Path.GetTempPath(), $"polyphony-journal-lineage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new JournalStore(Path.Combine(_tempDir, ".polyphony-state", "journal.db"));
    }

    [Fact]
    public async Task RecordStartAsync_AutoRecordsLineage_WhenRootIdPresent()
    {
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = "01JC1234567890ABCDEFGHJKMN",
                RootId = 4242,
                WorkItemId = 4242,
                Action = "plan_seed_children",
                Target = "wi:4242",
                StartedAt = startedAt,
            },
            CancellationToken.None);

        var lineages = await _store.GetLineagesAsync(4242, CancellationToken.None);
        lineages.Count.ShouldBe(1);
        var lineage = lineages[0];
        lineage.RunId.ShouldBe("01JC1234567890ABCDEFGHJKMN");
        lineage.RootId.ShouldBe(4242);
        lineage.CreatedAt.ShouldBe(startedAt);
        lineage.RetiredAt.ShouldBeNull();
        lineage.RetiredReason.ShouldBeNull();
        // We auto-populate host/user from Environment.
        lineage.CreatedByHost.ShouldNotBeNullOrEmpty();
        lineage.CreatedByUser.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task RecordStartAsync_NoRootId_DoesNotInsertLineageRow()
    {
        await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = "01JCNNNNNNNNNNNNNNNNNNNNNN",
                RootId = null,
                WorkItemId = 999,
                Action = "diagnostic",
                Target = "wi:999",
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            CancellationToken.None);

        // GetLineagesAsync queries by rootId so we can't easily probe
        // for "no row at all" — assert by querying both candidate roots.
        (await _store.GetLineagesAsync(999, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecordStartAsync_RepeatedSameRunAndRoot_IdempotentlyKeepsFirstObservation()
    {
        var first = 1_000_000L;
        var second = 2_000_000L;
        await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = "01JCAAAAAAAAAAAAAAAAAAAAAA",
                RootId = 100,
                Action = "first",
                Target = "wi:100",
                StartedAt = first,
            },
            CancellationToken.None);
        await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = "01JCAAAAAAAAAAAAAAAAAAAAAA",
                RootId = 100,
                Action = "second",
                Target = "wi:100",
                StartedAt = second,
            },
            CancellationToken.None);

        var lineages = await _store.GetLineagesAsync(100, CancellationToken.None);
        lineages.Count.ShouldBe(1);
        lineages[0].CreatedAt.ShouldBe(first);
    }

    [Fact]
    public async Task GetLineagesAsync_ReturnsAllLineagesForRoot_OrderedByCreatedAt()
    {
        await _store.RecordLineageAsync("01JCBBBBBBBBBBBBBBBBBBBBBB", 200, host: "h1", user: "u1", CancellationToken.None);
        await Task.Delay(5);
        await _store.RecordLineageAsync("01JCCCCCCCCCCCCCCCCCCCCCCC", 200, host: "h2", user: "u2", CancellationToken.None);

        var lineages = await _store.GetLineagesAsync(200, CancellationToken.None);
        lineages.Count.ShouldBe(2);
        lineages[0].RunId.ShouldBe("01JCBBBBBBBBBBBBBBBBBBBBBB");
        lineages[1].RunId.ShouldBe("01JCCCCCCCCCCCCCCCCCCCCCCC");
        lineages[0].CreatedByHost.ShouldBe("h1");
        lineages[0].CreatedByUser.ShouldBe("u1");
    }

    [Fact]
    public async Task RetireLineageAsync_StampsRetiredAtAndReason_AndReturnsTrue()
    {
        await _store.RecordLineageAsync("01JCRETIREDRETIREDRETIREDR", 300, host: null, user: null, CancellationToken.None);

        var retired = await _store.RetireLineageAsync("01JCRETIREDRETIREDRETIREDR", 300, reason: "reset apex", CancellationToken.None);
        retired.ShouldBeTrue();

        var lineages = await _store.GetLineagesAsync(300, CancellationToken.None);
        lineages.Count.ShouldBe(1);
        lineages[0].RetiredAt.ShouldNotBeNull();
        lineages[0].RetiredReason.ShouldBe("reset apex");
    }

    [Fact]
    public async Task RetireLineageAsync_AlreadyRetired_ReturnsFalseAndPreservesOriginalRetirement()
    {
        await _store.RecordLineageAsync("01JCDOUBLERETIREDDOUBLERETI", 400, host: null, user: null, CancellationToken.None);
        var firstReason = "first reason";
        (await _store.RetireLineageAsync("01JCDOUBLERETIREDDOUBLERETI", 400, firstReason, CancellationToken.None)).ShouldBeTrue();
        var firstSnapshot = (await _store.GetLineagesAsync(400, CancellationToken.None))[0];

        await Task.Delay(5);
        var second = await _store.RetireLineageAsync("01JCDOUBLERETIREDDOUBLERETI", 400, reason: "second reason", CancellationToken.None);
        second.ShouldBeFalse();

        var afterSecond = (await _store.GetLineagesAsync(400, CancellationToken.None))[0];
        afterSecond.RetiredAt.ShouldBe(firstSnapshot.RetiredAt);
        afterSecond.RetiredReason.ShouldBe(firstReason);
    }

    [Fact]
    public async Task RetireLineageAsync_NonexistentLineage_ReturnsFalse()
    {
        var retired = await _store.RetireLineageAsync("01JCMISSINGMISSINGMISSINGMI", 500, reason: null, CancellationToken.None);
        retired.ShouldBeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }
}
