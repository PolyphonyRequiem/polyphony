---
doc_type: index
status: exploratory
synopsis: Archive index for the May 2026 multi-agent investigation of compiler-lite (declaring workflows in C# and generating workflow YAML).
---

# Workflow Compiler-Lite Evaluation — Archive

Archived discussion materials from the May 2026 multi-agent investigation of the question:

> *"Should polyphony declare workflows in C# and use that to generate workflow YAML just-in-time, eventually evolving into a workflow compiler for specific shapes of work?"*

These artifacts are inputs to the parent ADO Feature, **not** a decision. The Feature kicks off with a deep re-evaluation from whatever position polyphony finds itself in at the time of re-engagement. The recommendations below may be partially or entirely obsolete by then — Phase 3/4 of the journal track, AB#3255/#3256/#3258 sibling tracks, on_error migration, and reset hardening are all expected to ship in the interim and will move the substrate.

## Layout

| Path | Authored by | Purpose |
|---|---|---|
| `01-prior-art.md` | Investigator 1 | Typed-host-language → emitted-runtime-artifact patterns (Bazel/Pulumi/CDK/Dagger). |
| `02-type-system-mechanics.md` | Investigator 2 | C# fluent DSL + Roslyn source-gen + analyzer mechanics. |
| `03-conductor-relationship.md` | Investigator 3 | Generate YAML / replace conductor / hybrid — Option A wins. |
| `04-agent-contracts.md` | Investigator 4 | Unifying agent output schema + route vocab + verb bindings as one C# contract. |
| `05-roadmap-cost.md` | Investigator 5 | Compiler-lite first, ride AB#3255/#3256, only pilot full compiler after. |
| `reviews/R1-type-correctness.md` | Reviewer 1 | Type-correctness lens. Verdict: qualify. |
| `reviews/R2-agentic-engineering.md` | Reviewer 2 | Agentic AI engineering lens. Verdict: qualify. |
| `reviews/R3-long-term-architecture.md` | Reviewer 3 | Long-term architecture lens. Verdict: qualify. |
| `synthesis.html` | Synthesizer (Opus high-reasoning) | The presented HTML deck. Proposes D14 ADR draft. |
| `critiques/D1-grumpy-operator.md` | Rubber duck (operator pain lens) | QUALIFY — 8 operator-grade conditions. |
| `critiques/D2-hostile-architect.md` | Rubber duck (steelman against) | DO-NOT-BUILD — counter-proposal: finish sibling tracks first. |
| `critiques/D3-implementation-realist.md` | Rubber duck (commit-shape lens) | SHIP-WITH-SCOPE-CUT — Phase 1 = one PowerShell lint over schema catalog. |
| `critiques/D4-llm-skeptic.md` | Rubber duck (LLM reality lens) | NARROW-SCOPE — drop "agent contract" framing, promote evals to milestone. |
| `critiques/D5-journal-integrator.md` | Rubber duck (D13 integration lens) | NEEDS-EXPLICIT-WIRING — compiler-lite consumes D13, must not duplicate. |

## Convergent thesis (across investigators, reviewers, ducks)

1. Whatever ships should be **build-time**, not JIT.
2. Conductor stays as runtime; compiler emits YAML conductor consumes.
3. YAML stays operator-readable; checked-in artifacts are first-class.
4. C# proves *shape*; journal/postconditions prove *effect*.
5. The "agent contract" framing is over-broad — narrow to deterministic perimeter (route vocab, output projection, malformed-output policy, typed fixtures).
6. Phase 1 must be small enough to ship as a single PR and prove the lint catches a real mutation before any further investment.
7. Three pilot gates: unit + golden-diff + real-conductor harness. **All three only matter if CI auto-discovers them** — past lint rot is the cautionary tale.
8. D13 effect-model authority is verb-level and runtime; compiler-lite may consume D13 metadata for kind-level capability lint but must not duplicate or override it.

## Divergent positions (worth re-litigating)

| Question | Synthesis stance | Strongest pushback |
|---|---|---|
| Is the "agent contract boundary" the highest-value target? | Yes | D4: no — it's the *deterministic perimeter* around agent outputs; the LLM itself can't be typed. |
| Is Phase 1 (typed contract registry + Roslyn analyzers) commit-sized? | Yes, 3-4 sprints | D3: no — Roslyn analyzers alone are 60-100 hours; ship a PowerShell lint first. |
| Should we build compiler-lite at all? | Yes, qualified | D2: no — finish AB#3255/#3256/#3258 + Conductor schema publishing first; reopen only after. |
| Does compiler-lite help with the past-week operator pain? | Partially | D1: mostly no — pain audit table shows it touches drift but not effects/CI/policy/release coupling. |
| How does compiler-lite integrate with D13/journal/drift/reset? | Mentioned in appendix | D5: under-specified; needs an explicit D14 clause on authority boundary. |

## Posture on re-engagement

When the parent ADO Feature is picked up, the **first task is a deep re-evaluation** of these materials against the then-current state of polyphony, **not** a direct implementation pass. The intermediate ships (journal Phase 3/4, AB#3255 typed contracts, AB#3256 matrix collapse, AB#3258 script→verb migration, on_error/AB#3257 unblock, reset fixes AB#3245/#3246) may already address 50-80% of the original framing's pain. The right disposition may be:

- **No compiler-lite needed** — the smaller fixes closed the wounds. Archive this evaluation, file a postmortem on the original framing.
- **Compiler-lite tier 1 only** — ship D3's PowerShell-lint Phase 1, measure for 1-2 quarters, do not promote further.
- **Compiler-lite tier 2** — extend to Roslyn analyzers and typed contract registry; one leaf workflow pilot.
- **Full DSL pilot** — only if tier 2 demonstrably catches a class of defects nothing else caught, *and* the team is sized to absorb the platform cost.

The default disposition should be the first or second option. The third and fourth options require explicit evidence that the lighter interventions were insufficient.

## What was NOT investigated (open in the re-eval)

- Cost of contributing route-condition exhaustiveness lint upstream to conductor itself (vs wrapping in a parallel C# system).
- Whether `Polyphony.SchemaGenerator` + `VerbOutputSchemaCatalog` substrate already covers 70% of the lint surface with no new platform.
- Eval infrastructure for agent contracts (D4 flagged as a gap).
- Release-cut tooling and provenance manifest design (D1 flagged as a class of pain the synthesis doesn't address).
- CI test-discovery sentinel (could ship independently of any compiler decision).

## Process artifacts

- All 5 investigations and 3 reviews were run as parallel agent jobs (claude-sonnet / gpt-5.5 / opus mix). Synthesizer was Opus high-reasoning. Rubber ducks were gpt-5.5.
- Each reviewer was told to anticipate the others' positions and respond preemptively.
- Rubber ducks were given the synthesis HTML + the investigation/review corpus and pushed to find blind spots; one was specifically grounded in Daniel's recent operational pain (the past-week incidents).
- This is the documented procedure for "convene the council" on a polyphony architectural decision — preserved here as an example for future use.
