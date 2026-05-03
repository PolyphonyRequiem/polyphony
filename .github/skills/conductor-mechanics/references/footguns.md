# Conductor Footgun Index

Tight, scannable list of every gotcha covered (or referenced) in the
`conductor-mechanics` skill. Use this when you're mid-debug and need to
recognize a symptom fast, or pre-merge to scan for known traps.

Severity legend: ★ nuisance · ★★ silent wrong-answer risk · ★★★ workflow-breaking.

## By symptom

| Severity | Error / symptom | Root cause | See |
|---|---|---|---|
| ★★★ | `TemplateError: 'dict object' has no attribute 'X'` on a script-agent field | Wrote `agent.X` instead of `agent.output.X` | [M1](m01-agent-output-shapes.md) |
| ★★★ | `TemplateError: 'dict object' has no attribute 'X'` on an LLM-agent field that the prompt clearly returns | LLM agent has no `output:` schema; response landed in `output.text` (Claude) or `output.result` (Copilot) | [M2](m02-llm-output-schemas.md) |
| ★★★ | Same template breaks when you switch `provider:` between Claude and Copilot | No-schema fallback key differs by provider (`text` vs `result`) | [M1](m01-agent-output-shapes.md), [M2](m02-llm-output-schemas.md) |
| ★★★ | `ValidationError: missing field <X>` after the LLM responded with most fields | Every declared `output:` field is required; no optional-field syntax exists | [M2](m02-llm-output-schemas.md) |
| ★★★ | `TemplateError: no filter named 'from_json'` or `'tojson'` | Neither filter is registered; only `json` and custom `default` are | [M3](m03-eval-context.md) |
| ★★★ | `for_each source: "{{ ... }}"` rejected at config load with identifier-check error | `source:` is a bare dotted path, not a Jinja expression | [M8](m08-parallel-and-foreach.md) |
| ★★★ | `for_each` body with inline `type: workflow` rejected by validator | Inline sub-workflow fan-out not supported; needs script wrapper or static parallel block | [M8](m08-parallel-and-foreach.md) |
| ★★★ | `TemplateError` when reading `group.failed_count` / `succeeded_count` | Groups expose only `outputs`/`errors`/`count`; derive counts as `errors \| length` | [M8](m08-parallel-and-foreach.md) |
| ★★ | Workflow-scope `output: x: "{{ a == b }}"` arrives at parent as truthy `"True"`/`"False"` string | `_maybe_parse_json` only coerces lowercase; Python booleans render capital | [M7](m07-output-map-vs-schema.md) |
| ★ | `context_window: 1000000` (or any other non-schema field) on an agent silently ignored | Not a recognized `AgentDef` field; no warning at validate | (no full doc; cite `config/schema.py` AgentDef) |
| ★★★ | `ValueError: No matching route found.` after a script returns an unexpected value | Routes don't cover the full output domain; no catch-all | [M4](m04-routing-rules.md) |
| ★★★ | Workflow ends silently at an agent, parent can't read its output | Agent has no `routes:` table → defaults to `$end` | [M4](m04-routing-rules.md) |
| ★★★ | `routes:` table on a `human_gate` is silently ignored | Gates route via `options[i].route`, not the router | [M1](m01-agent-output-shapes.md), [M4](m04-routing-rules.md) |
| ★★★ | `Model "<X>" is not available` at first LLM call | Model name not in provider's namespace | [M5](m05-models-and-providers.md) |
| ★★★ | `runtime.provider: openai-agents` validates clean, dies at run with `NotImplementedError` | Provider exists in schema enum but factory raises | [M5](m05-models-and-providers.md) |
| ★★★ | Workflow halts after ~10 nodes (sometimes prompts to extend) | Default `max_iterations: 10` is too low; parallel-of-N counts as N iterations | [M9](m09-limits-retries-checkpoints.md) |
| ★★★ | `TemplateError` when reading `parallel_group.output.foo` | Parallel/for-each groups have `outputs:`/`errors:`/`count:`, no `output:` key | [M8](m08-parallel-and-foreach.md) |
| ★★★ | For-each loop body sees wrong values for `workflow` / `output` / `_index` | Used a reserved name as `as:` — silently shadowed | [M8](m08-parallel-and-foreach.md) |
| ★★★ | `FileNotFoundError: [WinError 2]` for `command: foo` on Windows | `foo.cmd`/`foo.bat` on PATH; conductor doesn't honor PATHEXT | [M6](m06-cross-platform.md) |
| ★★ | `when: "{{ output.notes }}"` fires when `notes == "None"` or whitespace | `evaluate_condition` coerces non-empty strings to `True` | [M3](m03-eval-context.md) |
| ★★ | Sub-workflow output `version: "1"` arrives at parent as `int(1)` | Workflow-scope `output:` auto-coerces JSON/booleans/numbers | [M7](m07-output-map-vs-schema.md), [M9](m09-limits-retries-checkpoints.md) |
| ★★ | Sub-workflow's `<sub>.output.field` evaluates to undefined | Sub-workflow forgot to export `field:` in its workflow-scope `output:` map | [M7](m07-output-map-vs-schema.md) |
| ★★ | `output.foo` works in agent A's route but blank in agent B's prompt | `output` shorthand is current-agent only; B must use `A.output.foo` | [M3](m03-eval-context.md) |
| ★★ | Pricing/context-window comes back wrong for a 1M-context model | Name not in `engine/pricing.py:DEFAULT_PRICING`; fuzzy match resolved to a smaller model | [M5](m05-models-and-providers.md) |
| ★★ | Typo'd model name fails partway through a multi-agent run | Models not validated at workflow load — only at first agent call | [M5](m05-models-and-providers.md) |
| ★★ | Script's JSON output overwrites `stdout`/`stderr`/`exit_code` | JSON merge happens after built-in capture; only debug-logged | [M1](m01-agent-output-shapes.md) |
| ★★ | Resume from checkpoint fails after a cosmetic edit | Checkpoint hash includes workflow file content; whitespace edits invalidate | [M9](m09-limits-retries-checkpoints.md) |
| ★★ | Sub-workflow path resolution breaks after moving the parent file | `workflow:` paths are relative to the parent YAML directory | [M9](m09-limits-retries-checkpoints.md) |
| ★★ | MCP tools that need env vars silently fail under Copilot provider | Copilot SDK drops `env:` from MCP server configs (copilot-sdk#163) | [M5](m05-models-and-providers.md) |
| ★★ | `{{ planner.output.items }}` returns the value at key `items`, not `dict.items` method | `_DictSafeEnvironment` overrides attribute lookup | [M3](m03-eval-context.md) |
| ★★ | Agent that omits `tools:` accidentally has access to ALL workflow tools | `tools: <missing>` ≠ `tools: []`; missing inherits all, empty grants none | (no full doc; cite `executor/agent.py:38-91`) |
| ★ | Custom `default` filter triggers on `None` (not just undefined) | Differs from stock Jinja; usually desirable | [M3](m03-eval-context.md) |
| ★ | Checkpoints in `$TMPDIR` lost on system cleanup or reboot | Storage location `$TMPDIR/conductor/checkpoints/` is volatile | [M9](m09-limits-retries-checkpoints.md) |
| ★ | `for_each source: my.list` rejected at config load | `source:` requires ≥3 dotted segments | [M8](m08-parallel-and-foreach.md) |

## By design decision

| If you're about to… | Reach for… |
|---|---|
| Add a route on a field an LLM emits as JSON | Declare the agent's `output:` schema first ([M2](m02-llm-output-schemas.md)) |
| Reference an agent's exit code | Use `agent.output.exit_code` ([M1](m01-agent-output-shapes.md)) |
| Branch on a script's `phase` field | Confirm the script prints clean JSON to stdout, not stderr ([M1](m01-agent-output-shapes.md)) |
| Add the third or fourth `when:` route | Stop and add a catch-all ([M4](m04-routing-rules.md)) |
| Pick a model | Confirm the namespace matches the provider AND the name is in `engine/pricing.py:DEFAULT_PRICING` ([M5](m05-models-and-providers.md)) |
| Use a high-precision Claude model in a Copilot workflow | Per-agent `provider: claude` override instead of switching globally ([M5](m05-models-and-providers.md)) |
| Add a tool wrapper to PATH | Make it a real `.exe`, not a `.cmd` ([M6](m06-cross-platform.md)) |
| Compose a sub-workflow | Define the sub's workflow-scope `output:` map first ([M7](m07-output-map-vs-schema.md)) |
| Add a `parallel:` or `for_each:` block | Bump `max_iterations` ([M9](m09-limits-retries-checkpoints.md)); read group results as `<group>.outputs.<name>` ([M8](m08-parallel-and-foreach.md)) |
| Write a `when:` clause | Use explicit comparisons; never rely on string-truthiness ([M3](m03-eval-context.md)) |
| Add an LLM agent that hits a flaky endpoint **and you need stronger than 3-attempt default** | Declare `retry:` override ([M9](m09-limits-retries-checkpoints.md)) |
| Use a `from_json` filter | Don't — it doesn't exist; emit JSON from the producing script and let it merge ([M1](m01-agent-output-shapes.md), [M3](m03-eval-context.md)) |

## Pre-merge checklist

Before merging a workflow YAML change, scan for:

- [ ] Every `{{ <agent>.output.<field> }}` reference: does the source agent
      declare that field (LLM `output:` schema) or print it (script JSON)?
- [ ] Every `{{ <group>.output.<x> }}` reference on a parallel/for-each
      group: rewrite as `<group>.outputs.<name>.<x>`.
- [ ] Every `<agent>.exit_code` reference: replace with `<agent>.output.exit_code`.
- [ ] Every `routes:` table: enumerate every value the source field can
      produce, including error/unknown; confirm a matching `when:` or a
      final catch-all.
- [ ] Every regular agent without a `routes:` table: confirm `$end` is the
      intended terminal (and add an explicit `routes: [{to: $end}]` for clarity).
- [ ] Every `human_gate`: confirm `options[i].route` is set per option;
      confirm no stray `routes:` table is present.
- [ ] Every `model:` directive: matches the target provider's namespace AND
      appears (or fuzzy-resolves) in `engine/pricing.py:DEFAULT_PRICING`.
- [ ] `runtime.provider:` is `copilot` or `claude` — never `openai-agents`.
- [ ] `runtime.limits.max_iterations:` is set explicitly (default 10 is too low).
- [ ] Every `command:` directive on Windows targets: resolves to a real
      `.exe`, or routes through `pwsh -c`.
- [ ] Every `for_each as:` value: not `workflow`, `context`, `output`,
      `_index`, `_key`, or any agent name in the workflow.
- [ ] Every `for_each source:` value: at least 3 dotted segments.
- [ ] Sub-workflow boundaries: every field the parent reads (`<sub>.output.X`)
      is exported in the sub's top-level `output:` map.
- [ ] `output:` blocks are in the right scope (top-level = Jinja template
      map; nested under an agent = typed schema).
- [ ] `tools:` is declared explicitly (don't leave it unset and hope —
      missing inherits all workflow tools, which is rarely what you want).
- [ ] No `from_json` or `tojson` filter (neither exists); use script-stdout-merge or `| json` instead.
- [ ] Every `for_each source:` value: bare dotted path, NOT wrapped in `{{ }}`.
- [ ] No `for_each` body with inline `type: workflow` (validator rejects).
- [ ] No `group.failed_count` / `succeeded_count` references (use `errors | length`).
- [ ] Workflow-scope `output:` values producing booleans: render lowercase
      (`| string | lower`) or use `1`/`0` to avoid capital `"True"`/`"False"`.
- [ ] Agent-level fields are all in the `AgentDef` schema (no rogue
      `context_window:` or other unrecognized keys — silently ignored).
- [ ] No `when:` clauses that rely on string-truthiness (`when: "{{ output.foo }}"`)
      unless `output.foo` is a declared boolean.

## Validation gaps (ideas for upstream conductor)

Things `conductor validate` could catch but currently doesn't:

1. Template references `{{ <agent>.output.<field> }}` against the source
   agent's declared `output:` schema (or absence thereof).
2. `for_each as:` values that collide with reserved names.
3. `routes:` tables on `human_gate` agents (currently silently ignored).
4. `runtime.provider: openai-agents` (validates clean, runtime crashes).
5. `agent.model:` names that don't appear in `DEFAULT_PRICING` (warning).
6. Default `max_iterations: 10` when the workflow contains parallel/for-each.
7. `tools: <missing>` vs `tools: []` (intent ambiguous).
8. Use of `from_json` or `tojson` filter (neither exists).
9. `when:` clauses that depend on string-truthiness against an undeclared
   schema field (i.e., guaranteed to be `False` or guaranteed to fire on `"None"`).
10. `for_each source:` wrapped in `{{ }}` — should be a bare dotted path.
11. `for_each` body with inline `type: workflow` — currently rejected;
    consider whether the validator should suggest a workaround in the error.
12. Workflow-scope `output:` template strings containing `==`/`!=`/`is`/`and`/`or`/`>`/`<` — render as `"True"`/`"False"`/etc., which `_maybe_parse_json` doesn't coerce. Either extend `_maybe_parse_json` (one-line patch to recognize `"True"`/`"False"`/`"None"`) or warn at validate.
13. Agent-level fields not in `AgentDef` schema (e.g. `context_window:`) — currently silently ignored; should warn at validate.
