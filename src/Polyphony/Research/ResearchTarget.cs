namespace Polyphony.Research;

/// <summary>
/// Validated, immutable representation of a research storage location.
/// Built from the <see cref="Configuration.ResearchConfig"/> YAML DTO after
/// validation passes. Consumed by <see cref="IResearchStorage"/>
/// implementations and any code that needs to know where research
/// artifacts live.
/// </summary>
/// <param name="Repository">
/// Research archive repo in <c>owner/repo</c> format.
/// </param>
/// <param name="Branch">
/// Target branch in the research repo (e.g. <c>main</c>).
/// </param>
/// <param name="Platform">
/// Canonical lowercase platform identifier: <c>github</c> or <c>ado</c>.
/// </param>
public sealed record ResearchTarget(string Repository, string Branch, string Platform);
