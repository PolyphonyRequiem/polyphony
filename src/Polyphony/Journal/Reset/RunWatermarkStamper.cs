using Polyphony.Infrastructure.Processes;
using Polyphony.Tagging;

namespace Polyphony.Journal.Reset;

/// <summary>
/// Stamps the per-root run watermark tag
/// (<c>polyphony:run-started-at=&lt;UTC&gt;</c>) on a root work item.
///
/// <para>The watermark is the signal observers consult to discriminate
/// current-run PRs from prior-run PRs (see
/// <c>docs/decisions/run-reset.md</c> and
/// <see cref="Polyphony.Tagging.PolyphonyTags.RunStartedAtPrefix"/>).
/// <c>polyphony reset root</c> is the sole writer — the state phase of
/// the projection executor invokes this stamper after the per-resource
/// delete loop completes, so the new watermark always supersedes any
/// pre-existing one in the same transaction.</para>
///
/// <para>Fail-loud: twig errors propagate up to the executor's outer
/// catch, which marks the state phase as failed. This is symmetric to
/// <c>PlanObserver.ReadRunStartedAtAsync</c>'s fail-closed read posture —
/// the watermark is load-bearing, so a silent stamp failure must not be
/// indistinguishable from a successful stamp.</para>
/// </summary>
public interface IRunWatermarkStamper
{
    Task StampAsync(int rootId, DateTimeOffset utcNow, CancellationToken ct = default);
}

public sealed class RunWatermarkStamper(ITwigClient twig) : IRunWatermarkStamper
{
    private readonly ITwigClient _twig = twig;

    public async Task StampAsync(int rootId, DateTimeOffset utcNow, CancellationToken ct = default)
    {
        // Mirror AdoWorkItemTagDeleter: pre-read sync so PatchFieldsAsync
        // does not clobber tags written to ADO since the last cache refresh.
        await _twig.SyncAsync(ct).ConfigureAwait(false);

        var item = await _twig.ShowAsync(rootId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"twig show returned null for root work item {rootId}; cannot stamp run-started-at watermark.");

        var raw = item["tags"]?.GetValue<string>()
            ?? item["fields"]?["System.Tags"]?.GetValue<string>();
        var tags = TagSet.Parse(raw);

        // Strip any pre-existing watermark tags (including duplicates that
        // could arise from operator edits or prior reset bugs). The
        // projection delete phase only removes journal-observed watermark
        // resources; this defensive strip covers the unjournaled / manual
        // case so the post-stamp state is exactly one watermark tag.
        var stripped = tags
            .Where(tag => tag.StartsWith(PolyphonyTags.RunStartedAtPrefix + "=", StringComparison.Ordinal))
            .ToArray()
            .Aggregate(tags, static (current, existing) => current.Remove(existing));

        var stamped = stripped.Add(PolyphonyTags.RunStartedAt(utcNow));

        await _twig.PatchFieldsAsync(
            rootId,
            new Dictionary<string, string> { ["System.Tags"] = stamped.Format() },
            ct).ConfigureAwait(false);
        await _twig.SyncAsync(ct).ConfigureAwait(false);
    }
}
