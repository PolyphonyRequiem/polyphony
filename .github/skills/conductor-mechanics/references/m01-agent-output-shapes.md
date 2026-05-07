# M1: Agent Output Shapes

Script, LLM, and human_gate agents all write to `ctx[agent_name]`, but with
different inner shapes. Templates and route conditions must address them by
their actual shape.

## The container

Every agent's results are stored as:

```python
ctx[agent_name] = {"output": <agent-specific dict>}
```

So **always** access fields as `agent_name.output.<field>`, never
`agent_name.<field>`. (See M2 for the LLM-agent special case where `<field>`
is named per the schema.)

## Script agents

Script agents (`type: script`) produce an `output` dict with these keys:

```yaml
output:
  stdout: "<full stdout text>"
  stderr: "<full stderr text>"
  exit_code: 0           # or non-zero on failure
  # ...plus every key from any JSON object printed to stdout, merged in flat
```

The JSON merge is the subtlety: if your script prints
`{"ready": true}` to stdout, the resulting `output` dict is
`{stdout: "{...}", stderr: "", exit_code: 0, ready: true}`. So
in templates you write `preflight_check.output.ready`.

**Don't try `output.stdout | from_json` — there is no `from_json` filter
registered.** Only `json` (serialization) and a custom `default` filter exist
(see M3). The canonical pattern is "make the script emit valid JSON and let
the engine merge it."

**JSON-stdout merge can shadow built-ins.** If your script prints
`{"stdout": "x"}` (or `stderr` / `exit_code`), the merged `output` dict
**overwrites** the captured built-in. The engine debug-logs the collision
but does not block. Don't reuse those three names as JSON keys.

**Reference points to a known-good source:**
- `conductor-fix/src/conductor/engine/workflow.py:1807-1828`:
  where `output_content.update(parsed)` does the JSON merge.
- `conductor-fix/src/conductor/engine/context.py:209-238`:
  `ctx[agent] = {"output": output}` — the canonical wrapping.

## Parallel and for-each groups

`parallel:` and `for_each:` blocks **break the output-wrapper rule.** They
store results as:

```python
ctx[group_name] = {"type": "parallel", "outputs": {...}, "errors": {...}, "count": N}
```

There is **no `output:` key**. Read these as `group.outputs.<name>.<field>`
and `group.errors.<name>`, not `group.output.foo`. See M8 for full details.

## LLM agents

LLM agents (`type: agent`) shape their `output` dict according to whether
the agent declares an `output:` schema:

| Schema declared? | Provider | Shape of `output` |
|---|---|---|
| No | `claude` | `{"text": "<entire raw response as string>"}` |
| No | `copilot` | `{"result": "<entire raw response as string>"}` |
| Yes | either | `{"<schema_field_1>": ..., "<schema_field_2>": ..., ...}` parsed from the model's JSON |

**The no-schema fallback is provider-dependent** (`providers/claude.py:1907-1921`,
`providers/copilot.py:670`). Switching `provider:` between Claude and Copilot
changes which key holds the raw string — templates referencing one or the
other will break across providers. Always declare an `output:` schema for any
LLM agent whose response is read structurally. See M2.

## human_gate agents

Gates (`type: human_gate`) **bypass the router entirely** — they route via
`option.route` declared on the chosen option, not via a `routes:` table on
the agent. See M4. Their output dict is:

```yaml
output:
  selected: "<the option.value the user picked>"
  # ...plus any form-input keys the gate collected via `additional_input`
```

Reference `gate.output.selected` (NOT `gate.output.choice`) in any
downstream agent's prompt or template:

```yaml
prompt: |
  User selected: {{ open_questions_gate.output.selected }}
  Notes: {{ open_questions_gate.output.notes | default('-') }}
```

Source: `engine/workflow.py:1724-1730`, `gates/human.py:28-43`.

## Don'ts

- ❌ `preflight_check.exit_code` — missing `.output.`
- ❌ `architect.plan` — missing `.output.`
- ❌ `script_agent.output.foo` when the script printed `{"foo": ...}` to
  stderr (only stdout JSON merges into `output`)

## Dos

- ✅ `preflight_check.output.exit_code`
- ✅ `preflight_check.output.ready` (when the script printed `{"ready": ...}`)
- ✅ `architect.output.plan` (when architect declares `output: { plan: { type: string } }`)
- ✅ `open_questions_gate.output.selected`
- ✅ `review_group.outputs.security_reviewer.score` (parallel group)

## Discovery
Found via Polyphony AB#2924 (`agent.exit_code` instead of `agent.output.exit_code`)
and AB#2927 (LLM agent missing schema produced `output.result` string).
