namespace Polyphony.Sdlc;

/// <summary>
/// One wave in <see cref="EdgeGraph.ToWaves"/> — a set of work items
/// whose entry requirements all become <c>Ready</c> at the same
/// topological depth.
/// </summary>
/// <remarks>
/// <para>
/// Wave 0 contains items with no inbound cross-item edges into their
/// entry requirements (typically the run root, plus any items whose
/// parent is non-plannable so no <c>children_seeded</c> blocks them).
/// Wave N contains items whose entry requirements depend on items
/// dispatched in waves 0..N-1.
/// </para>
/// <para>
/// Items within a wave are sorted by id ascending for determinism.
/// </para>
/// </remarks>
/// <param name="WaveIndex">Zero-based wave index.</param>
/// <param name="ItemIds">Work item ids dispatchable in this wave, sorted
/// ascending.</param>
public sealed record EdgeGraphWave(
    int WaveIndex,
    IReadOnlyList<int> ItemIds);
