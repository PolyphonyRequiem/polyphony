# M3: Template & Route Evaluation Context

What `output` resolves to depends on whether you're inside a route `when:`
clause **of the current agent** or referencing another agent. The router
builds a slightly different eval context than the workflow-level template
renderer.

## The two contexts

### Workflow-level templates (most places)

Most Jinja templates in a workflow YAML render against the global context:

```yaml
prompt: |
  The architect produced this plan: {{ architect.output.plan }}
  Reviewer score: {{ reviewer.output.score }}
```

In this context you **must** use the fully qualified `agent_name.output.field`
form. There is no implicit "current agent" — every reference is by name.

### Route `when:` clauses (special)

When the router evaluates an agent's `routes:`, it builds:

```python
eval_context = {**context, "output": current_output}
```

(See `conductor-fix/src/conductor/engine/router.py`, around lines 83-88.)

So inside a `when:` clause **on the agent that just emitted output**, both
forms work:

```yaml
- name: architect
  routes:
    - to: open_questions_gate
      when: "{{ output.open_questions | length > 0 }}"        # current-agent shorthand
    - to: review_group
      when: "{{ architect.output.open_questions | length == 0 }}"  # also fine
```

Picking the long form (`architect.output.foo`) keeps routes greppable and
uniform — recommended unless brevity in a tight predicate is more valuable.

## When the shorthand bites you

The shorthand `output.foo` is **only** the current agent's output. If a
later agent wants the architect's plan, it must use `architect.output.plan`
— there is no implicit memory of "the previous output".

```yaml
- name: reviewer
  prompt: |
    Review this plan:
    {{ architect.output.plan }}     # ✅ correct — name the source
    {{ output.plan }}               # ❌ this is reviewer's not-yet-emitted output
```

## Template engine quirks

The Jinja environment used by conductor (`executor/template.py:18-67`) is a
custom `_DictSafeEnvironment` with these gotchas:

### Only `json` and `default` filters are registered
`from_json`, `tojson` are **not** registered. Standard Jinja built-ins
(`length`, `replace`, `lower`, `upper`, `trim`, `join`, `split`, `map`,
`select`, etc.) work because they ship with Jinja itself. **Neither
`from_json` nor `tojson` exists** — both fail at render time with
`TemplateError: no filter named '<X>'`. Don't reach for either:

| You wanted to… | Do this instead |
|---|---|
| Parse JSON from a string | Make the producing script emit valid JSON; the engine auto-merges it (M1) |
| Serialize a dict for embedding in a shell command | Use `\| json` (the registered name); or write a script that emits the JSON |

### Custom `default` filter triggers on `None`, not just undefined
Stock Jinja's `default` only fires when the value is undefined.
Conductor's `default` also fires on `None`. So
`{{ x | default("y") }}` → `"y"` whether `x` is missing **or** `x = None`.
Almost always the desired behavior — but worth knowing if you've ever
relied on stock Jinja's stricter semantics.

### `StrictUndefined` is in force
Any reference to an undeclared key raises `TemplateError` at render time —
there is no silent empty-string fallback. Combined with the validator's
gap (M2: no template-vs-schema cross-check), missing fields surface as
runtime errors, not load errors.

### `_DictSafeEnvironment` overrides attribute lookup
`obj.foo` always prefers `obj["foo"]` over Python's `getattr(obj, "foo")`
(`executor/template.py:18-39`). Concrete consequence: if your output is
`{"items": [...]}`, then `{{ planner.output.items }}` returns the list —
not the `dict.items` method, which would be Jinja's stock behavior.

So you cannot accidentally call dict methods on outputs (`.items()`,
`.keys()`, `.values()`) inside templates. Use `| list` or other filters
instead.

### `evaluate_condition` boolean coercion (the silent gotcha)
For `when:` clauses, `evaluate_condition` (`executor/template.py:133-153`)
renders the expression to a string, then coerces:

| Rendered string | Result |
|---|---|
| `"true"`, `"1"`, `"yes"` (case-insensitive) | `True` |
| `"false"`, `"0"`, `"no"`, `""` | `False` |
| **anything else** | `bool(rendered_string)` — **non-empty = `True`** |

Practical effect: `when: "{{ output.notes }}"` is `True` whenever `notes`
is **any non-empty string**, including the literal `"None"`, `"n/a"`, or
even `"   "` (whitespace).

Always write explicit comparisons:
- ❌ `when: "{{ output.error_message }}"` — fires on `"None"` or `"   "`
- ✅ `when: "{{ output.error_message != '' and output.error_message != 'None' }}"`
- ✅ `when: "{{ output.has_error }}"` (where `has_error` is a declared boolean)

## Flat-context arithmetic mode (simpleeval)

Some `when:` clauses use simple arithmetic (e.g. `coverage >= 70`) without
the `{{ }}` Jinja delimiters. Conductor's `_flatten_context` exposes
flat keys here too, but the rules are different: prefer Jinja form unless
you specifically need simpleeval semantics.

```yaml
- to: synthesizer
  when: "coverage >= 70"           # simpleeval, flat context
- to: synthesizer
  when: "{{ output.coverage >= 70 }}"  # Jinja, full context — preferred
```

## Don'ts

- ❌ `output.foo` outside a route `when:` clause (no implicit current agent
  in workflow-level templates).
- ❌ `output.plan` in a downstream agent's prompt expecting the previous
  agent's plan.
- ❌ Mixing simpleeval and Jinja inside one expression.

## Dos

- ✅ Use `architect.output.plan` everywhere except brevity-critical inline
  routes on the source agent itself.
- ✅ Treat `output.foo` as syntactic sugar exclusive to the current agent's
  routes — write a comment if you use it.

## Discovery
Surfaced while diagnosing AB#2924 — the temptation to write `architect.foo`
or `output.plan` is high; conductor's two-context model needed to be
written down so I would stop guessing.
