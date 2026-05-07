# Phase 6 PR #6 of 8 — Per-item guidance extraction

Adds the **extraction** half of per-item guidance: a small string of
context the operator wants the agent to see for a specific work item
(e.g. _"Use Polly for retries"_ or _"Reviewer: pay extra attention to
error messages"_). Driver wiring (injection into the agent prompt) lands
in PR #5.

Implements Section 4 + open-question #5 of the Phase 6 design sketch.

## What this PR adds

### Source-of-record (`policy.yaml > guidance:`)

```yaml
schema_version: 1

# Default — works on any platform with a description field.
guidance:
  source: description_block

# OR opt into an ADO custom field:
guidance:
  source: ado_field
  ado_field_name: Custom.PolyphonyGuidance

# Per-type overrides (most-specific wins):
guidance:
  source: description_block
  by_type:
    Task:
      source: ado_field
      ado_field_name: Custom.TaskGuidance
```

`description_block` reads from a fenced HTML comment in the work item
description:

```
<!-- polyphony:guidance -->
arbitrary text here
<!-- /polyphony:guidance -->
```

Description content **outside** the block is not extracted — only the
fenced block is under the prompt-injection trust boundary.

### New CLI verb

```
polyphony guidance extract --work-item N [--policy .conductor/policy.yaml]
```

Output:

```json
{
  "work_item_id": 12345,
  "source": "description_block",
  "guidance": "Use Polly for retries.",
  "guidance_present": true
}
```

`guidance` is `null` (and omitted from JSON) when no guidance is present;
`guidance_present` is the boolean projection workflows can route on.

### Load-time invariant

`policy.yaml` with `source: ado_field` but no `ado_field_name` (anywhere
on the inheritance chain) is rejected at `PolicyLoader.ApplyBuiltInDefaults`
and surfaced through `polyphony policy validate` as a load error.

## Files

**New:**
- `src/Polyphony/Sdlc/GuidanceSource.cs` — string constants + `IsValid`
- `src/Polyphony/Sdlc/GuidanceConfig.cs` — resolved `(Source, AdoFieldName?)`
- `src/Polyphony/Guidance/GuidanceExtractor.cs` — pure `Extract(WorkItem, GuidanceConfig)`
- `src/Polyphony/Models/GuidanceExtractResult.cs` — JSON output shape
- `src/Polyphony/Commands/GuidanceCommands.cs` — `polyphony guidance extract` verb
- `tests/Polyphony.Tests/Guidance/GuidanceExtractorTests.cs` — 19 unit tests
- `tests/Polyphony.Tests/Policy/GuidancePolicyTests.cs` — 14 policy load + resolver tests
- `tests/Polyphony.Tests/Commands/GuidanceExtractTests.cs` — 7 end-to-end verb tests

**Modified:**
- `src/Polyphony/Policy/PolicyConfig.cs` — `Guidance` property + `GuidancePolicy` / `GuidanceRule`
- `src/Polyphony/Policy/PolicyLoader.cs` — defaults + `ValidateGuidance` invariant
- `src/Polyphony/Policy/PolicyResolver.cs` — `ResolveGuidance(scope)` overlay logic
- `src/Polyphony/Commands/PolicyCommands.cs` — `SnapshotGuidance` for `policy load`,
  `ValidateGuidanceForReporting` for `policy validate`
- `src/Polyphony/Models/PolicyLoadResult.cs` — `Guidance` field + `PolicyGuidanceSnapshot`
- `src/Polyphony/PolyphonyJsonContext.cs` — register new result types
- `src/Polyphony/Program.cs` — `app.Add<GuidanceCommands>("guidance");`

## Design notes

- **Most-specific wins overlay:** `guidance.by_type[Name]` overlays the
  workspace default per-field; a type that sets only `source` inherits
  the workspace `ado_field_name`.
- **`null` vs empty string:** the extractor returns `null` for "no
  guidance present" and `""` for "block exists but empty" — workflows
  can branch on `guidance_present` without false positives.
- **Multi-block concatenation:** when a description carries more than
  one fenced block they're joined with `\n\n---\n\n`.
- **Malformed blocks:** an opening tag without a closing tag is
  silently skipped; identical opening tags greedily pair with the next
  closer (documented in test
  `Extract_OpeningTagOnly_WithLaterCompleteBlock_GreedilyConsumesToFirstClose`).
  A first-class work-item-content linter is deferred to a future polish
  PR (see open question #5 in the design sketch).

## Test results

```
Passed!  - Failed: 0, Passed: 2615, Skipped: 0, Total: 2615
```

Baseline was ~2558 — this PR adds ~57 new tests across the three new
test files plus a small `GuidanceSource.IsValid` `[Theory]`.

## Out of scope (lands in sibling PRs)

- **PR #5** — driver wiring: actually injecting the extracted text into
  the planner / executor / reviewer prompts.
- **Future polish** — work-item-content linter that surfaces malformed
  opening-without-closing tags via `polyphony policy validate`.
