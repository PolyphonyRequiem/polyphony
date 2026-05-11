using Polyphony.Annotations;
using Polyphony.Research;

namespace Polyphony.Commands;

/// <summary>
/// Research verbs (<c>polyphony research ...</c>) — the archivist curation
/// and promotion writer that bridge scratch research artifacts into the
/// sibling research repo. Ships as part of #3075 (archivist + curation pass).
///
/// <para>All verbs in this group are routing-style: always exit 0; errors
/// surface via <c>error</c> + <c>error_code</c> in the JSON envelope.</para>
/// </summary>
[VerbGroup("research")]
public sealed partial class ResearchCommands(
    IResearchStore researchStore)
{
    private readonly IResearchStore _researchStore = researchStore;
}
