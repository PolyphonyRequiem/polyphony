# M2: LLM Agent Output Schemas

**The contract:** if your workflow reads `<agent>.output.<named_field>`
from an LLM agent, that agent **must** declare an `output:` schema in YAML
naming that field. Without the schema, conductor never parses the model's
JSON response — the entire string lands in `output.result` (Copilot) or
`output.text` (Claude); see M1 / M8 for the provider split.

## Symptom

```
TemplateError: 'dict object' has no attribute 'open_questions'
```

…even though the prompt clearly says `Return a JSON object with structure
{ plan, open_questions, ... }` and the LLM's response visibly contained
those fields.

## Why

Conductor decides at agent-init time whether to enter the JSON-parse-and-validate
code path. From `conductor/providers/claude.py` (and the symmetrical Copilot
provider):

```python
has_output_schema = agent.output is not None
# ...
if has_output_schema:
    response = await self._execute_with_parse_recovery(...)
else:
    # Raw string mode — emit_output result goes to output.result
    ...
```

No schema → no parsing → no named fields → templates blow up.

## Required YAML

```yaml
- name: architect
  type: agent
  model: claude-opus-4.7-1m-internal
  prompt: !file ../prompts/architect-plan-level.md
  output:                           # ← THIS BLOCK IS REQUIRED
    plan:
      type: string
      description: Markdown plan document
    open_questions:
      type: array
      description: Questions requiring user input before plan can finalize
      items:
        type: object
        properties:
          topic:
            type: string
          detail:
            type: string
  routes:
    - to: open_questions_gate
      when: "{{ architect.output.open_questions | length > 0 }}"
    - to: review_group
      when: "{{ architect.output.open_questions | length == 0 }}"
```

The supported `type:` values are `string`, `number`, `boolean`, `array`,
`object` (see `OutputField` in `conductor-fix/src/conductor/config/schema.py:59-83`).
Nested validation is supported via `properties:` (objects) and `items:` (arrays).
**There is no `enum`, `required`, `nullable`, `format`, or `pattern` field** —
the schema only carries `type`, `description`, `items`, `properties`.

## Every declared field is required

`validate_output` (`executor/output.py:36-65`) checks that **every** field
declared in the schema is present in the model's response. There is no
notion of an "optional" output field. Omitting a declared field crashes the
agent with `ValidationError`; the LLM is then re-prompted (parse-recovery).

If you genuinely need optional fields, model them as nested keys inside one
required `properties:` field, or omit the schema for that agent and accept
the raw `output.text`/`output.result` string.

## Validator does not cross-check templates against schemas

`config/validator.py` does **not** verify that `{{ planner.output.foo }}`
references match the planner's declared `output:` schema (or that the
planner has a schema at all). A typo or missing-schema reference fails at
runtime with `StrictUndefined`, not at `conductor validate`.

## Generic content fallback

If a provider's response isn't a dict and isn't valid JSON,
`AgentExecutor.execute` (`executor/agent.py:213-230`) wraps it as
`{"result": <raw>}` regardless of which provider produced it. So under
specific edge cases Claude can yield `output.result` instead of
`output.text`. The reliable contract: **declare an `output:` schema and stop
guessing** which fallback key applies.

## Schema must match the prompt's JSON contract

The prompt and the YAML schema must agree. If the prompt asks for
`{ plan, open_questions }` and the schema declares only `{ plan }`,
the validator will reject the response and parse-recovery will retry.

Keep the prompt's "Return a JSON object" section beside the schema in
review — they are two halves of one contract.

## How to audit existing workflows

For every site like `{{ <agent>.output.<field> }}` or
`when: "{{ <agent>.output.<field> ... }}"`, verify the named agent
declares an `output:` block with that field.

A regex starting point:

```powershell
grep -rn '\{\{\s*\w+\.output\.\w+' workflows
```

Cross-reference each match against the agent's `output:` block. Any miss
is a future TemplateError waiting for the workflow to reach that node.

## Don'ts

- ❌ Rely on the prompt alone — the prompt is a contract for the model, not
  for conductor's parser.
- ❌ Use `output.result | from_json` as a workaround. It hides the contract
  and doesn't get type validation.
- ❌ Mix script-agent flat-merge expectations with LLM-agent schema expectations.

## Dos

- ✅ Declare the schema next to the agent definition.
- ✅ Keep field names identical between prompt and schema.
- ✅ Use `array` with `items: { type: object }` for collections of records.
- ✅ Run `conductor validate <workflow>.yaml` — it will catch some shape
  mismatches but **not** the missing-schema-vs-template-reference case.

## Discovery
Polyphony AB#2927. Found after AB#2925 (model namespace) was fixed and the
architect agent successfully called the LLM (~167s, 58k in / 2.8k out)
producing a valid JSON response that conductor refused to parse.
