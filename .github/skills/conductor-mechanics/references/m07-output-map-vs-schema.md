# M7: Workflow `output:` vs Agent `output:`

The same key — `output:` — means two completely different things depending
on where it appears in the workflow YAML. Conflating them silently produces
the wrong runtime behavior.

## At workflow scope: a Jinja template map

A top-level `output:` block (sibling of `agents:`) is a **template map**
rendered at end-of-run. It assembles the workflow's outward-facing result
from agent outputs, using Jinja:

```yaml
output:
  merged: "{{ pr_lifecycle.output.merged | default(false) }}"
  observation_count: "{{ close_out.output.observations | length }}"
  final_pr_url: "{{ pr_merger.output.pr_url }}"
```

These are template strings, not type declarations. They run after the
workflow's terminal node. The result becomes the workflow's `output`
when invoked as a sub-workflow.

## At agent scope: a typed schema declaration

An `output:` block **inside an agent** is a typed schema. For LLM agents
it drives JSON parsing of the response (see M2). For other agent types it
documents the contract:

```yaml
- name: architect
  type: agent
  model: claude-opus-4.7-1m-internal
  output:                         # ← schema, not template
    plan:
      type: string
    open_questions:
      type: array
      items:
        type: object
```

Field values here are `OutputField` records (`type:`, `description:`,
`properties:`, `items:`), not Jinja templates.

## How to keep them straight

| Property | Workflow-scope `output:` | Agent-scope `output:` |
|---|---|---|
| Sibling of | `agents:` | `prompt:`, `tools:`, `routes:` |
| Field values | Jinja template strings | `OutputField` records (typed) |
| Evaluated when | Workflow terminates | Per agent execution |
| Drives | Sub-workflow's emitted `output` | LLM JSON parsing & validation |
| Reading sites | Parent workflow's templates | Routes, downstream agent prompts |

## Auto-coercion footgun (workflow-scope only)

Workflow-scope `output:` values pass through `_maybe_parse_json` after
rendering (`engine/workflow.py:3398-3421`). The function:

| Rendered string | Coerced to |
|---|---|
| `"true"`, `"false"`, `"null"` (lowercase only) | `True`, `False`, `None` |
| Numeric-looking (`"5"`, `"3.14"`) | `int` / `float` |
| JSON-shaped (starts with `{` or `[`, parses) | `dict` / `list` |
| Otherwise | passthrough string |

So a workflow `output:` like:

```yaml
output:
  version: "{{ planner.output.version }}"   # planner emits "1"
  succeeded: "{{ pr.output.merged }}"       # pr emits "false"
```

Yields `version = int(1)`, `succeeded = False` — **not strings**.
A parent workflow doing `when: "{{ child.output.version == '1' }}"` will
silently miss because it's comparing `int` to `str`.

To force a string, use a non-coercible form: `"v{{ planner.output.version }}"`
or a Jinja `string()` filter (if registered).

### Capital `True`/`False` is **not** coerced (the silent gotcha)

`_maybe_parse_json` only matches **lowercase** `"true"`/`"false"`/`"null"`.
But Jinja renders Python booleans as **capital** `"True"`/`"False"`. So:

```yaml
output:
  succeeded: "{{ score >= 70 }}"   # renders to "True" or "False" (capital)
```

…arrives at the parent as the **string** `"True"` or `"False"`. The string
is non-empty, so any `when: "{{ child.output.succeeded }}"` in the parent
fires regardless of the underlying value (M3 boolean-coercion gotcha).

Workarounds until upstream fixes `_maybe_parse_json` to recognize
`"True"`/`"False"`/`"None"`:

```yaml
output:
  # ✅ Force lowercase before _maybe_parse_json sees it:
  succeeded: "{{ (score >= 70) | string | lower }}"
  # ✅ Or convert to int 0/1:
  succeeded: "{{ 1 if score >= 70 else 0 }}"
```

This coercion does **not** happen at agent-scope `output:` (which is a
typed schema, not a template).

## Sub-workflow boundary

When workflow A calls workflow B as a sub-workflow:

```yaml
# in workflow A:
- name: implementation
  type: workflow
  workflow: implement-pg@polyphony
  inputs:
    work_item_id: "{{ workflow.input.work_item_id }}"
  routes:
    - to: close_out
      when: "{{ implementation.output.merged }}"   # ← reads B's workflow-scope output:
```

`implementation.output.merged` here is the `merged:` key from workflow B's
top-level `output:` map, not from any specific agent inside B. The Jinja
template at workflow scope produced it.

## Failure modes

- **Forget the workflow-scope `output:` map** → parent workflow can't read
  any field from the sub-workflow; routes referencing `sub.output.foo`
  silently evaluate to undefined.
- **Forget the agent-scope `output:` schema on an LLM agent** → see M2,
  every `agent.output.named_field` read fails with TemplateError.
- **Put Jinja templates inside an agent-scope `output:`** → schema
  validation rejects the malformed `OutputField`.
- **Put `OutputField` records (with `type:`) at workflow scope** → Jinja
  fails to render because it sees a dict where it expected a string.

## Don'ts

- ❌ Copy an agent-scope `output:` block to workflow scope (or vice versa).
- ❌ Assume the parent workflow can introspect a sub-workflow's internal
  agents — it can only see the sub-workflow's exported `output:` map.

## Dos

- ✅ Treat the workflow-scope `output:` as the sub-workflow's public API.
  Whatever the parent needs to read must be exported there explicitly.
- ✅ Treat each LLM agent-scope `output:` as the parser contract for that
  agent's JSON response.
- ✅ When in doubt, look at indentation: top-level (no agent name above
  it in the YAML) = template map; nested under `- name: foo` = schema.

## Discovery
Surfaced while writing AB#2927 — the bug there is the agent-scope schema
case (LLM agents emit `output.result` without it), and it became clear the
documentation needed to disambiguate from the more familiar workflow-scope
template map.
