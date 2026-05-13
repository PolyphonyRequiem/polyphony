# Research Assistant — Agent Guidance

## Archive-Only Constraint (first pass — AB#3133)

This agent operates in **archive-only mode**. The chosen archive surface is:

- **Filesystem tool** — read files, list directories, search for patterns
  in the local repository checkout. This is the primary research surface.
- **`gh` tool** — query GitHub API for PR history, commit messages, and
  issue metadata when relevant to the research topic.

The following are explicitly **not available** in this first pass:

- No live web searches or external HTTP calls
- No MCP server tools (e.g., ADO search, code intelligence)
- No `twig` CLI (no ADO work item reads beyond what `gh` can surface)

## Research Quality Guidelines

- **Cite specific files and line numbers** — every claim must be traceable.
- **Describe patterns, not just locations** — explain *what* the code does
  and *why* it follows that pattern, not just where it is.
- **Flag gaps** — if a topic has no relevant archive material, say so
  explicitly rather than speculating.
- **Stay within scope** — if `archive_scope.paths` is provided, prioritize
  those directories but don't ignore related code outside them if it's
  directly relevant.

## Output Shape

The findings document must be valid Markdown with this structure:

```markdown
# Findings

## Topic 1: <topic text>

<synthesized findings with code references>

## Topic 2: <topic text>

<synthesized findings with code references>

## Sources

- `path/to/file.cs:10-25` — <brief relevance note>
- `path/to/other.md` — <brief relevance note>
```

## Future Expansion (AB#3134 / AB#3135)

When the sufficiency check and deep-researcher escalation land, this agent
will gain:

- A sufficiency self-assessment before returning
- Escalation to `deep-researcher` when archive findings are insufficient
- Archivist write-back to cache findings for future runs
