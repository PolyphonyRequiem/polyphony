namespace Polyphony;

/// <summary>
/// Decision constants emitted by the archivist agent for each scratch artifact.
/// String constants (not enums) for AOT compatibility and forward extensibility,
/// following the established polyphony pattern (<see cref="Sdlc.Disposition"/>,
/// <see cref="Sdlc.ExecutionMode"/>).
/// </summary>
public static class ArchivistVerdict
{
    /// <summary>Artifact is valuable; promote to the curated knowledge base.</summary>
    public const string Keep = "keep";

    /// <summary>Artifact is irrelevant or low-quality; drop it.</summary>
    public const string Discard = "discard";

    /// <summary>Artifact has potential but needs deeper research. The
    /// expand loop is owned by task #3076 — this task only ensures the
    /// decision is emitted in a shape that task can consume.</summary>
    public const string Expand = "expand";

    /// <summary>
    /// Returns true if <paramref name="value"/> is one of the three canonical
    /// archivist verdict strings.
    /// </summary>
    public static bool IsValid(string? value) =>
        value is Keep or Discard or Expand;
}
