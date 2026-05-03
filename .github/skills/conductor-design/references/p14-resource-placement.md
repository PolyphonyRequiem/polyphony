# P14: Resource Placement

Every artifact in the conductor SDLC ecosystem has exactly one home. The home is
chosen by **who customizes it** and **who consumes it at runtime** — not by what
file extension it has.

## Placement matrix

| Resource | Home | Notes |
|---|---|---|
| Workflow YAMLs | registry `workflows/` | Loaded by conductor; versioned with the registry |
| Agent prompts | registry `prompts/` | Co-located with the workflows that load them via `!file ../prompts/<name>.md` |
| Workflow scripts (deterministic logic invoked by workflow nodes) | registry `scripts/` | Invoked via `{{ workflow.dir }}/../scripts/<name>.ps1` |
| Tests for registry artifacts | registry `tests/` | Pester tests for scripts and workflow lints |
| Examples / starting templates | registry `reference/` | **Read-only**, never loaded at runtime, must be marked as examples in a header comment |
| Polyphony CLI source | polyphony repo `src/` | The CLI itself |
| CLI helper scripts (humans/CI invoke directly) | polyphony repo `scripts/` | Distinct from workflow scripts; consumer is human, not conductor |
| Tests for polyphony CLI | polyphony repo `test/` | MSTest convention |
| Process configuration | consumer repo `.conductor/process-config.yaml` | Per-consumer work-item state machine + types |
| Type definitions & templates | consumer repo `.conductor/work-item-types/` | Per-consumer type semantics |
| Repo-specific agent guidance | consumer repo `.conductor/agent-guidance/` | Loaded by `load-agent-guidance.ps1` |
| Skills | consumer repo `.github/skills/<name>/SKILL.md` | GitHub convention; agent-side discovery |
| Tools (twig, gh, polyphony CLI) | PATH | Not artifacts |

## Decision rules

1. **One home per file.** No file lives in two places. Stale copies are a known
   failure mode — the moment two copies exist, they will diverge.

2. **Locality.** A test lives next to the file it tests. A prompt lives next to
   the workflow that loads it. Don't separate things that change together.

3. **Customization boundary.**
   - Per-consumer (process-config, type defs, agent guidance, skills) → consumer
     repo at a known path.
   - Reusable verbatim across consumers (workflows, prompts, workflow scripts)
     → registry.

4. **Workflow scripts live in the registry, not in consumer repos.** They are
   coupled to the workflows that invoke them — the script's stdout schema *is*
   the workflow's input schema. Coupling them physically matches the logical
   coupling. Bug fixes ship atomically with the workflow version.

5. **CLI helper scripts ≠ workflow scripts.** Both can be `.ps1`, but their
   consumers differ:
   - Workflow scripts → invoked by conductor inside a workflow node → registry.
   - CLI helper scripts → invoked by humans or CI from a terminal → polyphony repo.

6. **Tools live on PATH.** `twig`, `gh`, `polyphony` itself are not artifacts —
   they are dependencies. Workflows assume them present; preflight checks them.

7. **`reference/` is for examples only.** It must never be loaded at runtime.
   If a consumer needs a starting point for `.conductor/` files, copy from
   `reference/` once at bootstrap; thereafter the consumer owns the copy.

## Path convention for workflow scripts

Workflow nodes invoke registry-side scripts via:

```yaml
args:
  - "-NoProfile"
  - "-File"
  - "{{ workflow.dir }}/../scripts/<name>.ps1"
```

The `{{ workflow.dir }}/../scripts/` form is required because conductor's cwd is
the **consumer repo**, not the registry root. A bare `scripts/<name>.ps1` would
resolve against the consumer repo and fail (or worse, find a stale local copy).

## Anti-patterns

- A script invoked by both humans and a workflow node. Split it: one home in the
  registry (workflow), one in polyphony repo (CLI helper). Share library code
  via PATH-installed tools, not by duplicating files.
- A `reference/` file that gets loaded at runtime. Either it's authoritative
  (move to `scripts/` or `prompts/`) or it's an example (mark it so).
- A workflow script in the consumer repo. Migrate to the registry; if it truly
  needs per-consumer customization, that's a workflow-design problem, not a
  placement problem — express the customization through inputs or config files
  the script reads, not by forking the script.
