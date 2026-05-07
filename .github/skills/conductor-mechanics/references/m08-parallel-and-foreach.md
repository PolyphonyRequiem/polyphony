# M8: Parallel & For-Each Groups

Parallel groups and `for_each` blocks **break the standard agent output
shape** described in M1. They store results under `outputs:`, `errors:`, and
`count:` — there is no `output:` key on the group itself. Templates and
routes that read these groups must use the group-specific shape.

## Group output shape (`engine/workflow.py:1519-1607`)

```python
ctx[group_name] = {
    "type": "parallel",       # or "for_each"
    "outputs": {              # successful agent results, keyed by agent name (parallel)
        "agent_a": {...},     # or by item key (for_each)
        "agent_b": {...},
    },
    "errors": {               # failed agent results, same keying
        "agent_c": {"message": "...", ...},
    },
    "count": 3,               # total agents in the group
}
```

There is **no** `group_name.output.x` — there is only `group_name.outputs.x`,
`group_name.errors.x`, `group_name.count`.

### Author intuition trap: there is no `failed_count` / `succeeded_count`

A common mistake is to read group results as if there were aggregate counters:

```yaml
# ❌ These keys don't exist:
when: "{{ review_group.failed_count > 0 }}"
when: "{{ review_group.succeeded_count == review_group.count }}"
```

Derive them from `errors` and `count` instead:

```yaml
# ✅
when: "{{ review_group.errors | length > 0 }}"
when: "{{ review_group.errors | length == 0 }}"     # all succeeded
when: "{{ (review_group.count - (review_group.errors | length)) > 2 }}"
```

The mental model is "the engine partitions completed agents into successes
(`outputs`) and failures (`errors`); count is the total fan-out width." If
you find yourself wanting an aggregate counter, write the derivation inline.

## Failure modes (`workflow.py:2614-2874`, `2927-3278`)

Both `parallel:` and `for_each:` blocks accept a `failure_mode:` field with
three values:

| Mode | Behavior |
|---|---|
| `fail_fast` | First failure cancels remaining agents and propagates the error. Default for parallel. |
| `all_or_nothing` | Wait for all agents; if any failed, treat the entire group as failed. |
| `continue_on_error` | Run all agents; collect successes in `outputs`, failures in `errors`. Group always "succeeds". |

`continue_on_error` is the only mode where reading `group_name.errors` is
meaningful — the others either short-circuit or fail the whole group.

## For-each variable scoping (`config/schema.py:139`)

`for_each` declares an iteration variable name via `as:`. **The validator
does not reject reserved names** — using `as: workflow`, `as: context`,
`as: output`, `as: _index`, or `as: _key` silently shadows runtime context
inside the loop body, producing wrong values with no error.

```yaml
- name: process_items
  type: for_each
  source: workflow.input.items
  as: item             # ✅ safe
  # as: workflow       # ❌ shadows the workflow object
  # as: output         # ❌ shadows current-agent output
  # as: _index         # ❌ shadows the loop index
  body:
    - name: handle_one
      ...
```

Reserved names you must avoid as `as:`: `workflow`, `context`, `output`,
`_index`, `_key`, plus any agent name in the workflow.

## For-each source must be fully qualified (`config/schema.py:222-240`)

`source:` field must contain at least 3 dotted segments, e.g.
`workflow.input.items` or `planner.output.tasks`. A bare `source: items`
or `source: my.list` is rejected at config load.

### `source:` is a bare dotted path, NOT a Jinja expression

This is a high-confusion footgun: every other reference in workflow YAML
uses `{{ }}` Jinja syntax, so authors instinctively write:

```yaml
# ❌ Rejected at config load — first segment "{{ pg_dispatcher" fails the
#    Python identifier check
source: "{{ pg_dispatcher.output.pgs }}"
```

The actual contract is a bare dotted path:

```yaml
# ✅
source: pg_dispatcher.output.pgs
```

If you see `validate_source_format` complaining about an identifier check,
strip the `{{ }}` wrappers.

## For-each can fan out sub-workflows inline (since conductor PR #102)

The intuitive "fan out N sub-workflows from a list" pattern works:

```yaml
# ✅ Supported — `agent:` (singular) with `type: workflow` and
#    `input_mapping:` rendered against loop variables
- name: dispatch_pgs
  type: for_each
  source: planner.output.pgs
  as: pg
  agent:
    name: implement_one
    type: workflow
    workflow: ./implement-pg.yaml
    input_mapping:
      pg_id: "{{ pg.id }}"
```

This was historically rejected by the validator but is now supported (see
upstream conductor PR #102). The earlier limitation was real — workflows
written before the fix used wrapper scripts shelling out to `conductor run`.
Modern workflows should use the inline form; production example:
`.conductor/registry/workflows/plan-level.yaml:1740-1765` (the
`plan_children_group` block uses the top-level `for_each:` syntax with
the same `workflow:` + `input_mapping:` idiom against the loop variable).

The keys are **`agent:` (singular)** — not `body:` — and **`input_mapping:`**
— not `inputs:`. The loop variable (named via `as:`) and `_index` / `_key`
are available inside `input_mapping` Jinja expressions.

## `key_by:` for dict-keyed outputs

`for_each` accepts an optional `key_by:` Jinja expression that produces the
key under which each iteration's result lands in `outputs:`. Default is the
loop index. Useful when you want `outputs.<task_id>` rather than
`outputs.0`, `outputs.1`, …

```yaml
- name: process_tasks
  type: for_each
  source: planner.output.tasks
  as: task
  key_by: "{{ task.id }}"
  body: ...
```

Then read `process_tasks.outputs.task_42.<field>`.

## Routes on parallel/for-each groups

Parallel and for-each groups have their own route evaluators
(`engine/router.py: ParallelRouter`, `ForEachRouter`). `when:` clauses
on group-level routes can reference `outputs`, `errors`, `count`, but
**not** `output.<x>` — there is no `output` key on the group.

```yaml
- name: review_group
  type: parallel
  agents: [...]
  routes:
    - to: synthesize
      when: "{{ review_group.errors | length == 0 }}"   # ✅
    - to: triage
      when: "{{ review_group.outputs.security.score < 70 }}"   # ✅
    # - to: synthesize
    #   when: "{{ review_group.output.score > 70 }}"   # ❌ no .output key
```

## max_iterations counting

A parallel group of N agents counts as **N iterations** in one workflow tick.
This interacts with the default `max_iterations: 10` — see M9.

## Don'ts

- ❌ Reference `group.output.foo` — there is no `output:` key on a group.
- ❌ Read `group.failed_count` / `succeeded_count` — derive from `errors | length`.
- ❌ Use `as: workflow` / `as: output` / `as: _index` in for-each — silently
  shadows reserved names.
- ❌ Use a 1-segment `source:` — config load rejects it.
- ❌ Wrap `source:` in `{{ }}` — it's a bare dotted path.
- ❌ Use `body:` / `inputs:` syntax — the keys are `agent:` (singular) and `input_mapping:`.
- ❌ Assume `failure_mode` defaults to `continue_on_error` — it doesn't.

## Dos

- ✅ Read group results as `<group>.outputs.<name>.<field>` and
  `<group>.errors.<name>`.
- ✅ Pick `failure_mode` deliberately — `continue_on_error` for review fans,
  `fail_fast` for "any failure aborts" pipelines.
- ✅ Use `key_by:` to make outputs addressable by domain ID instead of index.
- ✅ Lift the workflow's `max_iterations` if you have parallel groups of more
  than ~5 agents.

## Discovery

Surfaced during conductor-fix research after the dogfood iteration's
architect-then-review fan-out kept exhausting the default 10 iterations.
