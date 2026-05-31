# Stravinsky — Conductor Gaps in the Agent-Prompt Contract

**Date:** 2026-05-31  
**Author:** Stravinsky (AI Agents Expert)  
**Requested by:** Daniel Green (offline, ~1h)  
**Scope:** The contract between conductor and the AI agents it spawns — what conductor gives the agent, what it takes back, where that contract is underspecified, leaky, or actively hostile to good agent design.

---

## Executive Summary (read this first)

Conductor's agent contract has two load-bearing holes that account for the majority of polyphony's dogfood pain: **(1) output schema declaration with no enforcement** and **(3) addendum/facet-profile injected as raw prompt text with no structural separation from conductor's native tooling**. These two together mean agents routinely deliver something that looks valid to the engine but is subtly wrong for the workflow, and there is no cheap mechanical gate to catch it. The other three gaps (context window, failure mode detection, retry state) are real but downstream of these two.

---

## Gap 1 — Output Schema: Declared, Not Enforced

**Impact rank: 1 | Cost to build: Medium**

### Today

Every `type: agent` node in polyphony's workflows carries an `output:` block that declares field names, types, and descriptions. Example from `implement-merge-group.yaml:685-697` (`root_reviewer`):

```yaml
output:
  verdict:
    type: string
    description: Review outcome — "approved" or "changes_requested"
  feedback:
    type: string
    description: Detailed review feedback
  issues:
    type: array
    ...
    items:
      type: string
```

And from `plan-level.yaml:656-665` (architect's discriminant field):

```yaml
research_request_kind:
  type: string
  description: >-
    Discriminant for the research payload. Required and ALWAYS
    present. One of:
      - "none"     — no archive research needed
      - "request"  — architect needs archive research
    Routing is a string compare on this field — no nested-optional
    dict access required.
```

### Why it hurts

**Conductor does not validate agent output against the declared schema.** The schema is documentation for workflow authors, not a runtime contract. Consequences:

1. **Enum-drift silent failures.** The `verdict` field above should be `"approved" | "changes_requested"`. If an agent emits `"approved "` (trailing space), `"Approved"` (capital A), or the field is missing entirely, conductor does not reject it. The routes downstream do a string-equality check (`{{ root_reviewer.output.verdict == 'approved' }}`). All three misspellings silently fall through to the catch-all route — which is `to: coder` per M4. A reviewer that emits a capitalization variant forever loops the coder, producing infinite revise cycles until `max_iterations: 200` fires (plan-level.yaml:47).

2. **Type coercion surprises.** `research_request_kind` is declared as a string with two allowed values. If the architect returns it as `None` (Python null via JSON null), Jinja2's `== 'request'` comparison evaluates to false. The workflow silently skips the research leg. The plan-level workflow hedges this in the comment "no nested-optional dict access required" — but that's a workflow-author workaround, not a runtime guarantee.

3. **Missing required fields.** `research_request_kind` is documented as "Required and ALWAYS present" — but conductor does not enforce required-ness. If the architect forgets it under a narrow code path (e.g., a very long prompt triggers mid-turn truncation before the research_request_kind is emitted), the field is absent and the routing behavior is undefined.

4. **Type mismatches for array fields.** `architect.output.children` is declared as `array`. If the architect emits `"children": null` or `"children": "none"` (a string describing emptiness), the downstream `for_each` iterator fails at runtime, not at schema-check time.

**The M2 requirement** ("declare all output fields used by routes") already exists and is enforced by workflow authors. But M2 is a documentation convention, not a validator. There is no V-rule that checks whether what the agent *actually returned* conforms to what its `output:` block declared.

**Token/retry cost of this gap:** Every schema violation triggers either a silent wrong-route or a catch-all that re-invokes the agent from scratch. Each fresh invocation of `architect` at `claude-opus-4.7` costs ~30-80K tokens in context alone. A single capitalization drift on `verdict` can loop coder+reviewer indefinitely.

### What "fixed" looks like

Conductor validates agent output against its declared `output:` schema before making the fields available to routes. Specifically:

- **Type checking:** `type: string` rejects null; `type: array` rejects string or null; `type: boolean` coerces `"true"`/`"false"` strings (per M7, which polyphony already handles via `| string | lower` in Jinja2) or rejects non-boolean values.
- **Enum validation:** An `enum:` sub-key on string fields (e.g., `enum: [approved, changes_requested]`) rejects unknown values and routes to a configurable `on_schema_error:` target, or emits a structured error the workflow can catch.
- **Required fields:** An `required: true` annotation on output fields causes conductor to reject the turn if the field is missing.
- **Partial output fallback:** When validation fails, conductor should expose `{field}_schema_error: true` alongside the raw value so a catch-all route can distinguish "schema violation" from "wrong answer."

The polyphony team's ADR for verb output schema registry (`docs/decisions/verb-output-schema-registry.md`) addresses the *script* side of this contract. The same principle needs to propagate into conductor's agent-output handling. The shapes already exist in every workflow; conductor needs to enforce them.

---

## Gap 2 — Context Window: No Help from Conductor

**Impact rank: 3 | Cost to build: High**

### Today

When an agent is re-invoked (revise loop, research loop, parent-patch loop), the Jinja2 prompt template accumulates all prior state. For `architect` in `plan-level.yaml:583`, the prompt is loaded from `prompts/architect-plan-level.md` — a 500+ line template that conditionally injects, depending on re-entry path:

- The **prior plan verbatim** (`{{ architect.output.plan }}` — a full Markdown plan document, potentially 3K-8K tokens)
- **Research findings** (`{{ research_assistant.output.findings }}` — structured Markdown with per-topic sections and source citations)
- **Reviewer feedback** (`{{ pr_feedback_analyzer.output.feedback_summary }}`)
- **Type definition** (`{{ type_loader.output.definition }}`)
- **Decomposition guidance** (`{{ type_loader.output.decomposition_guidance }}`)
- **Description template** (`{{ type_loader.output.template }}`)

There is no mechanism in conductor to:

1. **Warn** that the rendered prompt exceeds a token budget
2. **Summarize** prior-turn content (prior plan, prior research) before injection
3. **Truncate** gracefully when context is near the model's limit
4. **Route on context size** (e.g., "if the rendered prompt exceeds 150K tokens, route to a compression step first")

### Why it hurts

**Mid-turn truncation is the most dangerous failure mode.** When a rendered prompt approaches `claude-opus-4.7`'s 200K context limit, the model begins truncating its *output* mid-stream, not its *input* context. The agent returns a partially-formed JSON response. The `output.plan` field may be a valid-but-incomplete plan; `output.research_request_kind` may be missing entirely (see Gap 1's required-field consequence). The evidence floor check (`evidence_floor_check` in actionable.yaml:526) catches the "zero commits" case, but there is no equivalent catch for "plan written to disk but JSON output field was truncated."

**Token bloat from full-plan re-injection.** On a third revision cycle, the architect's context contains:
- The original plan (full verbatim)
- All three rounds of reviewer feedback
- Any research findings from prior loops
- The type definition + template (repeated every invocation)

A mid-complexity Issue plan at polyphony's typical plan depth runs 2K-5K tokens. Three revision rounds means 6K-15K tokens just for accumulated plans. The model wastes its "working memory" re-reading stable, already-accepted content rather than focusing on the narrow surgical changes the revise rules require.

**The `architect-plan-level.md` prompt already includes the mitigation principle** ("Surgical only. Address ONLY the issues the reviewer flagged") — but this is agent-side guidance, not engine-side enforcement. The engine doesn't know how big the context is or whether it's degrading.

### What "fixed" looks like

Conductor exposes token-budget metadata to the workflow — specifically, the rendered prompt's estimated token count and the model's context limit. Workflow authors can then route on it:

```yaml
- to: context_compression_step
  when: "{{ actionable_agent.estimated_tokens > 150000 }}"
```

More ambitiously: conductor could support a `context_budget:` annotation on agent nodes. When a re-invocation would exceed the budget, the engine applies a declared `summarize_with:` strategy (e.g., summarize `prior_agent.output.plan` via a cheap model before injection). This is non-trivial to implement but would eliminate the largest class of silent mid-turn truncations.

Near-term affordable version: emit a `conductor.agent_prompt_tokens` output field the workflow can log/gate on. Even observability alone would let the team triage truncation incidents post-hoc.

---

## Gap 3 — Addendum/Facet-Profile Composition: Prompt-Text Injection with No Structural Separation

**Impact rank: 2 | Cost to build: Low-Medium**

### Today

The `compose_addendum` step (actionable.yaml:265-276) calls `polyphony agent compose-addendum {work_item_id}` and produces:
- `output.facets` — list of item facets
- `output.skills` — skill names to consult
- `output.mcps` — MCP server names to lean on
- `output.guidance` — verbatim per-item guidance text
- `output.guidance_present` — boolean

The `actionable_agent` prompt template then injects all of this as **plain prompt text** inside `{% if %}` blocks (actionable.yaml:357-394). The result looks like:

```
## Skills available for this item

The skills below are bound to the item's facet set in
`process-config.yaml`. Consult their `SKILL.md` files in
`.github/skills/` before acting:

- telemetry
- prod-deploy
- teams-comments

## MCP servers recommended for this item

The MCP servers below are bound to the item's facet set. Use them
where applicable; the conductor runtime may have additional
servers connected, but these are the ones the operator declared
as relevant to this work:

- mcp-service-health
```

The conductor `tools:` list on the same node (actionable.yaml:329-332) is the *actual* connection list:

```yaml
tools:
  - twig
  - gh
  - filesystem
```

The comment at actionable.yaml:309-317 makes this gap explicit: *"The dynamic skills/MCPs the composer surfaces are advisory context the agent reads from its prompt — they do not change which servers conductor has connected."*

### Why it hurts

**Structural incoherence for the agent.** The agent receives two different lists of "what tools you have":

1. **What conductor actually connected** (the `tools:` list): `twig`, `gh`, `filesystem`
2. **What the addendum says to use** (advisory prompt text): `telemetry`, `prod-deploy`, `mcp-service-health`

The agent must reconcile these two lists. In practice, if the addendum-recommended MCP (`mcp-service-health`) is NOT in the conductor `tools:` list, the agent may attempt to call it and fail, or paper over the missing capability with "called the server but got a connection error." The comment instructs the agent to "not paper over a missing capability" — but the agent has no mechanical way to know which MCP servers are truly available unless it attempts a call and handles the error.

**Per-item guidance fights the system prompt.** The `guidance_loader` output is injected as `## Repo-Specific Guidance` sections (actionable.yaml:437-451). A repo-specific guidance file might say "always add the PR number to commit messages" — which directly contradicts the system prompt's `AB#{{ work_item_id }}` instruction. The agent receives two conflicting instructions at the same authority level (both are `##` headers in the same prompt). There is no clear precedence rule in the template — "Treat it as append-only context" (line 391) is editorial guidance, not a structural rule.

**Addendum guidance volume is unbounded.** The per-item guidance block (`compose_addendum.output.guidance`) is injected verbatim (line 393: `{{ compose_addendum.output.guidance }}`). If an operator has written 2KB of per-item guidance, the full 2KB appears mid-prompt. Combined with skills list, MCP list, repo-specific guidance (from `guidance_loader`), and the base prompt, the actionable_agent prompt can easily run 6K-12K tokens before the agent reads a single line of the work item.

**Skills are names, not content.** The addendum injects skill *names* (e.g., `telemetry`) and tells the agent to consult `SKILL.md` files in `.github/skills/`. But the agent must then use a filesystem tool call to read the skill file — adding latency and another potential failure point. If the skill file is missing, the agent silently proceeds without it. Conductor has no mechanism to pre-load the skill content at compose time.

### What "fixed" looks like

**Conductor needs structured `skills:` and `prompt_addendum:` fields on agent nodes.** The comment at actionable.yaml:52-57 names this gap directly: conductor's pydantic schema for v1.0.x has no `skills:` / `mcps:` / `prompt_addendum:` fields. Adding these would allow:

1. `skills:` — conductor resolves skill names to content at step-dispatch time, injecting the full skill text into a **dedicated, structurally-separated system prompt section**, not the user-visible prompt body. The agent does not need a filesystem tool call.
2. `mcps:` — conductor validates that listed MCP servers are actually connected (in the workflow-level `tools:` or a per-step override), and rejects/warns at dispatch time if a recommended MCP is not available. The agent's "tools I have" and "tools I should prefer" become a consistent single list.
3. `prompt_addendum:` — conductor provides a named slot for operator-authored guidance that is injected *after* the system prompt and *before* the user turn, with explicit precedence over the base prompt. The agent receives a clear signal: "addendum overrides."

Absent conductor changes, the near-term polyphony-side fix is: **rank the sections by explicit precedence headers** in the prompt template. Add a single paragraph at the top of the template: "When Repo-Specific Guidance conflicts with these instructions, Repo-Specific Guidance wins on matters of convention and loses on matters of workflow mechanics." Currently there is no such rule.

---

## Gap 4 — Agent Failure Modes: Only One Mechanical Floor Exists

**Impact rank: 4 | Cost to build: Medium**

### Today

The `evidence_floor_check` (actionable.yaml:525-546) is the only mechanical post-agent validator in the entire workflow suite. It was added in Phase 6 PR #7 specifically because "agent crashed before producing anything" was a live failure mode. Its contract: ≥1 commit on the evidence branch AND non-empty PR body. Both checks are mechanical — they do not assess content quality.

For all other agents, conductor trusts the output completely:

- **`coder`** (implement-merge-group.yaml:420): After the coder runs, `branch_assert_on_impl_post_coder` (line 532) checks that git HEAD is still on the expected branch — a *structural* invariant, not a *content* check. The coder could commit an empty file or commit to a subdirectory that doesn't exist and the branch assertion would pass.

- **`root_reviewer`** (implement-merge-group.yaml:678): The reviewer returns `verdict: "approved"` or `verdict: "changes_requested"`. There is no mechanical check that the reviewer actually ran `git diff` before approving. The catch-all route on an unexpected verdict (`# Catch-all per M4: to: coder`) means a reviewer that returns `{}` (empty JSON) loops coder indefinitely.

- **`architect`** (plan-level.yaml:583): Returns a `plan` string. The plan is committed to disk by a downstream `write_plan` script step, which *could* check that the file is non-empty — but there is no documented evidence it does. The plan_reviewer then reviews it. An architect that returns an empty plan string → the plan file is written as empty → the reviewer reviews an empty file and likely blocks → revise loop starts with an empty prior plan.

- **`actionable_agent`** (actionable.yaml:325): Returns a `summary` string. The evidence floor check protects against "no commits" but does NOT protect against "agent committed a zero-byte file and wrote a plausible summary claiming success." The reviewer is the only downstream protection, and with the placeholder rubric (PR #8 deferred), it defaults to `approve`.

The failure modes catalog (`docs/polyphony-agent-failure-modes.md`) documents 6 modes but frames them as "what doc would have prevented this" — it's a postmortem document, not a mechanical detection layer.

**The six undocumented behavioral failure modes conductor cannot currently detect:**

| Mode | Example | Conductor sees |
|------|---------|----------------|
| Silent success | Agent reports `{"summary": "done"}` with no commits | Valid JSON, floor passes |
| Mid-turn truncation | `research_request_kind` missing from architect output | Missing field, M3 guards return default |
| Hallucinated success | Reviewer approves without reading diff | `verdict: "approved"` — valid |
| Catch-all misfire | Agent emits `verdict: "APPROVED"` (capitalized) | Catch-all routes to `coder` — treated as `changes_requested` |
| Branch drift | Coder commits to wrong branch | Floor check passes; branch assertion catches this post-coder |
| Schema drift | Agent emits `children: "none"` (string) | `for_each` fails at runtime, not at schema time |

### What "fixed" looks like

Three complementary defenses:

1. **Generalize the floor-check pattern.** `evidence_floor_check` already proves the pattern is valuable and cheap. Every `type: agent` node that commits to disk should have a corresponding floor-check script step. The check should be configurable: minimum commit count, minimum output field lengths, file existence.

2. **Conductor-native output field validators.** Tied to Gap 1 fix: once output schemas are enforced, conductor can reject a response before it reaches routes. The catch-all misfire (`"APPROVED"` vs `"approved"`) becomes a schema violation, not a silent wrong-route.

3. **Mandatory `agent_output_floor:` annotation on routing agent nodes.** A conductor YAML annotation that declares "this agent MUST return a non-empty value for field X before routes are evaluated." If the field is empty/null, conductor routes to a declared `on_floor_fail:` target. This is lower-overhead than a full schema validator — it's a presence check, not a type check.

---

## Gap 5 — Re-Spawning on Retry: Fresh Context, Partial History

**Impact rank: 5 | Cost to build: Low (polyphony side) / High (conductor side)**

### Today

When `revise_loop_gate` (actionable.yaml:736) routes back to `actionable_agent`, the workflow YAML comment is honest: *"only the agent re-runs."* The agent starts a **fresh conversation** with no memory of its prior turn. The only state available to it is what the Jinja2 template explicitly injects:

```yaml
{% if revise_loop_gate is defined %}
## Revise loop — previous reviewer feedback

{% if evidence_reviewer is defined %}{{ evidence_reviewer.output.comment }}{% endif %}
{% endif %}
```

This is **one reviewer comment from the most recent round only.** On a third revision cycle:
- The coder (`implement-merge-group.yaml:468-481`) receives only `root_reviewer.output.feedback` — the immediately prior reviewer feedback.
- The reviewer (`implement-merge-group.yaml:715-733`) receives `root_reviewer.output.feedback` as "your prior feedback" — also only the most recent round.

This means feedback accumulation works by overwrite, not by threading. If the reviewer in round 2 changed its focus (addressed concern A but raised concern B), the coder in round 3 sees only concern B. Concern A's resolution is invisible.

**The `architect-plan-level.md` template is the exception.** It checks `context.history[-1]` (the most recent node in the execution history) to determine re-entry type. This is a polyphony-side workaround using conductor's `context.history` API — a partial answer. But `context.history` contains only the list of node names, not their outputs. The template still manually reconstructs prior state from named output objects.

**What "fresh context" means for retry cost:**

| Agent | Model | Typical input tokens (fresh) | Input tokens on 3rd retry |
|-------|-------|------------------------------|---------------------------|
| `actionable_agent` | claude-opus-4.6 | ~3K | ~8K (3K base + 2K evidence + 3K reviewer comment) |
| `architect` | claude-opus-4.7 | ~15K | ~40K (15K base + 10K plan × 1 + 8K research + 7K feedback) |
| `coder` | claude-opus-4.6 | ~5K | ~10K (5K base + 3K reviewer feedback × 1 + 2K prior context) |
| `root_reviewer` | claude-opus-4.6 | ~20K+ | ~25K (code is re-read from disk every time via tool call) |

For the architect at round 3, the model is re-reading 40K tokens of mostly-stable context (base prompt, type definition, template, original plan) to make what the revise rules explicitly say should be "surgical, narrow" changes to 2-3 paragraphs.

**The state preservation problem has a more insidious failure mode:** if `evidence_reviewer.output.comment` is undefined (e.g., the evidence_reviewer crashed or returned an empty comment field), the `{% if evidence_reviewer is defined %}` guard silently omits the feedback block. The agent retries with no context about why it's retrying — its instructions become "perform the actionable work" with no indication that a prior attempt failed. The agent is likely to produce identical output to its failed prior attempt.

### What "fixed" looks like

**Conductor side:** A native retry envelope. When a node re-enters an agent that has prior turns, conductor injects a `conductor.prior_turns: [{output: {...}, iteration: N}]` object. The workflow template can then render a cumulative feedback thread rather than "the last reviewer comment only."

```yaml
{% for turn in conductor.prior_turns %}
### Iteration {{ turn.iteration }} feedback
{{ turn.reviewer_output.comment }}
{% endfor %}
```

This is analogous to how multi-turn agents work in other orchestration frameworks (e.g., LangGraph's message history). Conductor's current model is stateless per-invocation; adding turn history would be a meaningful contract change.

**Polyphony side (near-term, no conductor changes):** Accumulate feedback in a dedicated script step before each revise-loop re-entry. A `accumulate_feedback` script step reads all prior `evidence_reviewer.output.comment` values from a state file on disk and passes them as a consolidated block to the agent. The script step replaces the `{% if evidence_reviewer is defined %}` guard with a pre-composed, always-present feedback section. Cost: one new PowerShell helper, one new script step per revise loop. Patterns for this script shape already exist (e.g., `resolve-research-max-loops.ps1`).

---

## Bonus: Addendum/Facet-Profile Composition Pipeline — A Redesign Sketch

### How it should look with conductor support

**Polyphony's responsibility:** Compose the business logic of the addendum — map item facets → skill names, MCP names, per-item guidance, all via the existing `FacetProfileComposer` contract. Produce a typed envelope (`facets`, `skills`, `mcps`, `guidance`) that is version-stable and schema-validated. Polyphony owns this decisively; it knows the process config, the facet-to-tool mapping, and the operator's per-item guidance. The current `compose-addendum` verb is the right shape. What changes is the handoff format: rather than emitting flat strings for prompt injection, polyphony emits a structured artifact (JSON envelope) that conductor consumes via a native protocol.

**Conductor's responsibility:** Consume the polyphony envelope natively in the agent step's dispatch phase. The agent step gains a `facet_addendum:` key alongside `tools:`. Conductor resolves skill names to full skill content by reading the SKILL.md files at dispatch time. It validates that all MCP names in the addendum are present in the `tools:` list (or a workflow-level `mcps:` declaration), producing a structural error at dispatch rather than a runtime failure during the agent turn. The per-item guidance is injected into a dedicated system-prompt slot — structurally separate from the workflow author's base prompt and the agent's role instruction — with an explicit precedence rule: addendum guidance overrides conventions, loses on workflow mechanics. The agent receives a single coherent view: "tools I can call" = `tools:` list + validated MCP addendum; "guidance I follow" = base prompt < repo guidance < per-item guidance; "skills I consult" = pre-loaded skill content as named sections, no filesystem tool call required. This eliminates the dual-list incoherence, the unbounded guidance-injection risk, and the missing-skill-file silent-skip failure mode in a single design change.

---

## Gap Summary Table

| # | Gap | Impact | Cost | Primary consequence |
|---|-----|--------|------|---------------------|
| 1 | Output schema declared, not enforced | **Highest** | Medium | Silent wrong-route; infinite revise loops; enum drift undetected |
| 2 | Context window — no conductor help | Medium | High | Mid-turn truncation; token waste on re-invocation |
| 3 | Addendum injected as prompt text | **2nd highest** | Low-Med | Dual tool-list incoherence; guidance fights system prompt; skill lookup requires extra tool call |
| 4 | Agent failure modes — one floor check only | Medium | Medium | Silent success; hallucinated approval; zero-commit agent proceeds to review |
| 5 | Retry spawns fresh, partial history only | Lower | Low/High | Identical re-attempt with no feedback context; feedback overwrites not threads |

---

## Evidence Ledger (concrete YAML citations)

- `actionable.yaml:50-57` — explicit note that conductor lacks `skills:` / `mcps:` / `prompt_addendum:` schema fields
- `actionable.yaml:309-317` — "tools: list is the static set... dynamic skills/MCPs are advisory context the agent reads from its prompt"
- `actionable.yaml:357-394` — addendum injected as `{% if %}` blocks in the user-visible prompt
- `actionable.yaml:427-451` — guidance_loader sections injected as `## Repo-Specific Guidance` headers with no precedence rule
- `actionable.yaml:525-546` — `evidence_floor_check` — the only mechanical post-agent validator in the suite
- `actionable.yaml:719-723` — catch-all route on `evidence_reviewer` for "unknown / missing decision"
- `implement-merge-group.yaml:685-697` — `root_reviewer` output schema (verdict, feedback, issues)
- `implement-merge-group.yaml:788-795` — catch-all route on reviewer: unexpected verdict → coder (silent `changes_requested`)
- `plan-level.yaml:591-703` — full `architect` output schema; note `research_request_kind` declared "Required and ALWAYS present" with no enforcement
- `plan-level.yaml:72-77` (`architect-plan-level.md:72-77`) — prior plan injected verbatim on re-entry
- `actionable.yaml:736-769` — `revise_loop_gate`; only most-recent `evidence_reviewer.output.comment` injected
- `implement-merge-group.yaml:468-481` — coder only sees most-recent `root_reviewer.output.feedback`
- `implement-merge-group.yaml:715-733` — reviewer on re-review only sees its own previous feedback
- `docs/polyphony-conductor-directory.md:278-282` — `profile.yaml` is "reserved placeholder"; no live consumer today

---

*Filed by Stravinsky, 2026-05-31. Cross-seam dependencies: Mahler owns conductor-engine side of Gaps 1/2/5; Wagner owns workflow-YAML side of Gaps 3/4; Bach's verb-output-schema-registry ADR is a direct analogue to Gap 1 on the script side.*
