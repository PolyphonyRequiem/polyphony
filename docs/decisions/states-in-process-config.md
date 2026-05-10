# Decision: state→category mapping lives in process-config.yaml

**Date:** 2026-05-10
**Status:** Accepted
**Issue:** [#281](https://github.com/PolyphonyRequiem/polyphony/issues/281)

## Context

Polyphony's routing logic needs to classify every work-item state into one of five
canonical categories: `Proposed`, `InProgress`, `Resolved`, `Completed`, `Removed`.
The `PhaseDetector` and `TransitionValidator` use these categories to decide
whether an item is plannable, implementable, complete, etc.

Until this change, polyphony classified states by calling
`StateCategoryResolver.Resolve(stateName, entries: null)` in twig, which
falls back to a hardcoded heuristic based on common state-name substrings
(`active`, `doing`, `committed`, `closed`, `done`, …). This heuristic worked for
the four standard ADO process templates but silently failed for any custom state
vocabulary. Concretely: CloudVault uses `Started`, `Completed`, `Cut` for its
custom CMMI types — none of which the heuristic recognized. Items in those
states routed as `Unknown` and the workflow stalled.

The bypass had been in place since polyphony's first routing implementation. The
fix needed to either (a) plumb authoritative `StateEntry` data from ADO through
to all four callsites, or (b) move the mapping into config so it's user-owned
and the runtime never needs to guess.

## Decision

**State→category mapping is a per-type block in `process-config.yaml`.** Each
type that has transitions MUST declare every state name an item of that type
can occupy, with its category:

```yaml
states:
  Bug:
    Active: in_progress
    Resolved: resolved
    Closed: completed
```

`ProcessConfig.GetCategory(typeName, stateName)` is the single lookup path.
`StateCategoryResolver` calls (and the runtime heuristic that backs them) are
removed from polyphony entirely. `TransitionValidator` and `PhaseDetector` now
resolve categories via the injected `ProcessConfig`.

A new validator rule **V-21** is an error (not a warning):

- Type declared in `transitions:` but absent from `states:` → V-21
- A state's category is empty or not one of the five canonical names → V-21
- A transition target name is not declared in the type's `states:` block → V-21

V-21 is a hard preflight error rather than a warning because the runtime cost
of an undeclared state is silent routing failure — the workflow returns
`Unknown` and operators have no signal that anything is wrong until items
mysteriously stop progressing.

## Alternatives considered

**A. Plumb authoritative ADO `StateEntry` list through to every `Resolve()`
callsite.** This would let twig's `StateCategoryResolver` use the per-type
official ADO state metadata (which already encodes category). Rejected because:

1. Adds runtime dependency on ADO API per routing decision (latency, failure mode)
2. Doesn't help repos that want a different category mapping than ADO's defaults
3. Couples polyphony's routing to twig's process-fetch infrastructure
4. Doesn't surface mismatches at config time — still a runtime-only failure

**B. Extend twig's `FallbackCategory` heuristic to know more state names.**
Considered briefly. Rejected because: (1) the list grows without bound as new
templates and custom vocabularies appear; (2) it's still implicit/hidden; and
(3) the `entries: null` bypass is a polyphony-side bug that twig can't fix.

**C. Make the validator rule a warning.** Rejected. The whole point of the
preflight check is to catch silent runtime failures at config time. A warning
that ships to production is the same outcome as no check at all — you find out
when items fail to route days later.

## Migration

- **Existing repos using the implicit heuristic**: validate-config will now
  emit V-21 errors. Add a `states:` block declaring every state per type.
  Bootstrap-conductor.ps1 generates a correct block for the four standard
  templates; custom templates require manual authoring.
- **Tests using `ProcessConfigBuilder`**: builder auto-synthesizes a `states:`
  block from a well-known catalog covering the four standard templates.
  Tests with custom vocabulary (CMMI's `Started`/`Cut`, etc.) call
  `WithStates(typeName, dict)` for explicit overrides. The catalog lives only
  in test scaffolding — production code has zero hardcoded state heuristics.
- **CV-side companion**: cloudvault-service-api's bootstrap PR adds a `states:`
  block declaring CV's CMMI custom states (`Proposed`, `Committed`, `Started`,
  `Completed`, `Cut`; Bug uses `Active`, `Resolved`, `Closed`).

## Consequences

**Positive:**
- Routing decisions are deterministic and inspectable. `polyphony validate-config`
  catches the misconfigured-state class of bug at preflight.
- No runtime ADO calls for routing. No fallback heuristic anywhere in the code.
- Repos with custom state vocabularies (CMMI variants, ServiceNow imports,
  GitHub Projects, etc.) work without touching polyphony source.

**Negative:**
- Existing repos must add a `states:` block. Bootstrap helps for the four
  standard templates; custom templates require operator authoring.
- Documentation footprint increases (this ADR, schema doc V-21 row, agent-failure
  modes section retired).

## See also

- `docs/polyphony-process-config-schema.md` — V-21 row and `states:` schema
- `docs/polyphony-agent-failure-modes.md` § 6 — historical failure mode (now
  structurally closed by V-21)
- `src/Polyphony/Configuration/ProcessConfig.cs` — `States`, `GetCategory`,
  `ParseCategory`
- `src/Polyphony/Configuration/ConfigValidator.cs` — V-21 implementation
