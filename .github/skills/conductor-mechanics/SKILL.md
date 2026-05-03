---
name: conductor-mechanics
description: Conductor runtime contract and YAML mechanics. Load when authoring, debugging, or modifying conductor workflow YAML — anything that touches agent output access, route conditions, models, or cross-platform invocation. This skill captures the runtime plumbing that lint and YAML-schema validation cannot catch. Companion to `conductor-design` (principles); load *this* for plumbing changes, *that* for design changes.
---

# Conductor Workflow Mechanics

These are the conductor runtime contracts you cannot infer from reading the
YAML — every item here was learned the hard way from a workflow that lint-passed
and validated, then exploded at execution.

**Scope:** plumbing, parser contract, runtime field shapes, route evaluation,
model/provider coupling, cross-platform invocation. **Not** design philosophy
— for that, see [`conductor-design`](../conductor-design/SKILL.md).

**When to load:**

| Change type | Load this skill | Load `conductor-design` |
|---|---|---|
| Adding/editing an agent's `output:` schema | ✅ | — |
| Changing a route `when:` condition | ✅ | — |
| Renaming an agent or its output field | ✅ | — |
| Adding a new agent type (script/LLM/human_gate) | ✅ | maybe |
| Adding a `parallel:` or `for_each:` block | ✅ | maybe |
| Choosing a model | ✅ | — |
| Setting `max_iterations`, `retry:`, or other limits | ✅ | — |
| Adding a new gate or workflow node | maybe | ✅ |
| Naming a new agent | — | ✅ |
| Restructuring re-entry behavior | maybe | ✅ |

---

## M1: Agent Output Shapes
Script vs LLM vs human_gate agents store results differently. Templates and
route conditions must address them by their actual shape, not the obvious one.

→ [Full details](references/m01-agent-output-shapes.md)

## M2: LLM Agent Output Schemas
LLM agents that emit JSON **require** an explicit `output:` schema in YAML.
Without it, the entire response goes to `output.result` as a raw string and
every `agent.output.named_field` reference fails with `TemplateError`.

→ [Full details](references/m02-llm-output-schemas.md)

## M3: Template & Route Evaluation Context
What `output.foo` resolves to in a route `when:` clause depends on whether
you're in the current agent's eval context or referencing another agent.
Get this wrong and conditions silently evaluate to nonsense.

→ [Full details](references/m03-eval-context.md)

## M4: Routes Have No Implicit Fallback
Every reachable agent state needs an explicit `when:` route or you get
`ValueError: No matching route found`. Catch-alls are not free.

→ [Full details](references/m04-routing-rules.md)

## M5: Provider & Model Namespaces
Models live in the provider's namespace, not Anthropic's API namespace. There
is no `--model` override on `conductor run`. Switching providers requires a
workflow rewrite (or a provider-side alias layer).

→ [Full details](references/m05-models-and-providers.md)

## M6: Cross-Platform Subprocess Footguns
Conductor's Python subprocess on Windows does **not** honor `PATHEXT`.
`command: foo` won't find `foo.cmd` or `foo.bat`, only `foo.exe`. Wrappers
must publish a real `.exe` or be invoked through `pwsh -c`.

→ [Full details](references/m06-cross-platform.md)

## M7: Workflow `output:` vs Agent `output:`
The same key means two completely different things at workflow scope vs
agent scope. The workflow-level map is Jinja templates rendered at
end-of-run (and **auto-coerces** booleans/numbers/JSON-shapes); the
agent-level map is a typed schema declaration that drives JSON parsing
of the response.

→ [Full details](references/m07-output-map-vs-schema.md)

## M8: Parallel & For-Each Groups
Parallel and `for_each` blocks store results under `outputs:`/`errors:`/`count:`,
**not** `output:`. For-each `as:` silently shadows reserved names like
`workflow`, `output`, `_index`. `failure_mode:` defaults matter.

→ [Full details](references/m08-parallel-and-foreach.md)

## M9: Limits, Retries & Checkpoints
Default `max_iterations: 10` is almost always too low (parallel-of-N counts
as N). Retries are opt-in per-agent. Checkpoints live in `$TMPDIR` and
invalidate on whitespace edits. Sub-workflow paths are parent-relative.

→ [Full details](references/m09-limits-retries-checkpoints.md)

---

## Quick Footgun Index

A tighter, scannable list of every gotcha covered above — for when you're
mid-debug and need to recognize a symptom fast.

→ [`references/footguns.md`](references/footguns.md)
