# ADR: Workflow Path-Coverage Harness (MVP)

> **Status:** Accepted, MVP shipped 2026-05-10 across PRs #269, #270, #271.

## Context

Until 2026-05, the only way to validate a polyphony workflow change end-to-end
was to dogfood it against a real ADO work item. That meant:

- Real LLM calls (cost, non-determinism, network dependence).
- Real ADO writes (state changes that must be cleaned up).
- Real git mutations (branch fanout, PR creation, eventual rebase pain).
- A multi-minute-to-multi-hour cycle per change.

Several real-incident bug classes — outer-loop infinite cycles, workflow YAML
field-reference drift, gate routing surprises — surfaced only after a multi-
hour dogfood had already burned. Catching them with a tighter feedback loop
was overdue.

The four strategies considered (PR #159's wake):

1. **Mechanical state-effects catalog** — `docs/polyphony-state-effects-catalog.md`.
   Shipped, but documentation-only; no automatic enforcement.
2. **Runtime trace mode** — issue #162. Deferred.
3. **Property-based / state-machine workflow testing** — issue #163. Deferred.
4. **Formal concurrency model** — issue #164. Deferred.

This ADR adds a fifth: **scenario-based path coverage**. Not a substitute for
1–4 — a complement that ships today.

## Decision

Build an in-process Python test harness that:

1. Constructs a real conductor `WorkflowEngine` against the real workflow
   YAML files in `.conductor/registry/workflows/`.
2. Substitutes the LLM provider with a scripted `FakeProvider` that returns
   pre-baked agent outputs in the order conductor calls them.
3. Substitutes the gate handler with conductor's built-in `--skip-gates`,
   which auto-picks the first option on every human gate.
4. Substitutes external CLIs (`polyphony`, `twig`, `gh`) with a single .NET
   shim binary staged on PATH that matches argv against a per-scenario
   manifest and emits scripted stdout / exit codes.
5. Executes the workflow and asserts the terminal node id matches the
   scenario's expected terminal.
6. Runs as part of every CI build via the existing Pester step in `ci.yml`,
   with conductor installed from `git+https://github.com/microsoft/conductor.git@main`.

## Why in-process Python (not subprocess conductor + fake LLM endpoint)

Conductor exposes `WorkflowEngine(provider=...)` as a public constructor
kwarg. Substituting the provider that way is **one line of Python**, requires
no network mocking, and gives us deterministic agent-output replay with no
serialization surface to debug.

The alternative — running conductor as a subprocess and pointing it at a fake
HTTP endpoint — would have required us to intercept and emulate the OpenAI /
Anthropic / Azure protocols, multiplying the surface area of "test
infrastructure that can break independently of the system under test." The
harness must be cheaper to maintain than the bugs it catches; in-process is
the only way to keep that ratio in our favor.

## Why a .NET shim binary (not Python script substitution)

Conductor calls scripts via
`asyncio.create_subprocess_exec(rendered_command, *rendered_args, env={...})`
with no shell. PATH lookup applies. The cleanest interception is to put a
binary on PATH whose argv0 disambiguates which CLI is being called and whose
manifest tells it what to return.

A single binary copied under three names (`polyphony`, `twig`, `gh`)
shares a manifest reader and matcher. Self-contained single-file publish per
RID is mandatory because framework-dependent publish creates an apphost shim
that breaks under the rename-and-copy pattern. .NET 11 makes this small and
fast.

A Python alternative (`#!/usr/bin/env python` shim scripts staged in a tempdir)
would have worked on Linux but would not have worked the same way on Windows
without a shim launcher, and would have lost the argv0-based dispatch.
Single-binary won.

## Why `--skip-gates` (and the limitation it imposes)

Conductor already auto-picks the first option of every human gate when
launched with `--skip-gates`. This is free, requires no new conductor
surface, and fits the MVP. The cost: scenarios cannot exercise non-default
gate branches today.

A custom `HumanGateHandler` for in-process invocation is an obvious
follow-up; the upstream conductor `/api/gate` POST endpoint (issue #272) is
the production-side counterpart.

## Why CI install via git URL (not PyPI)

The PyPI package named `conductor-cli` is an unrelated project by Geoffrey Yu.
We cannot publish ours under that name. CI installs from
`git+https://github.com/microsoft/conductor.git@main`, which is public and
auth-free. Pinning to a SHA is the natural next step if conductor's main
becomes unstable.

## Consequences

### Positive

- Path coverage on every PR build, ~5–10s per scenario.
- Workflow YAML changes can no longer silently regress a known-good path.
- New incidents become reproducible: write a scenario, watch it fail, fix
  the workflow, watch it pass — same cycle as a unit test.
- The harness scaffolding is small (~1500 LOC of Python + a few hundred
  lines of C#), so its own maintenance cost is low.

### Negative / accepted

- **Scenarios are happy-path on day one.** Two scenarios (`close_out_happy_path`,
  `cascade_remedy_no_stale`) prove the rails work. Real coverage compounds
  scenario-by-scenario over time.
- **Non-default gate routing is unsupported.** Tracked under issue #272 +
  follow-up custom handler.
- **Sub-workflow path coverage is untested.** Sub-workflows load with their
  own provider/registry; needs verification once a scenario exercises one.
- **Conductor `main` is a moving target.** A breaking upstream change will
  break our CI. Acceptable: we'll see it on the next PR build and fix-forward.
- **Adds ~30s to CI per build.**

## Alternatives considered and rejected

- **Mock conductor at the engine boundary.** Would have made the test
  infrastructure non-isomorphic with the real engine, defeating the whole
  point.
- **Run a real LLM with low temperature against canned prompts.** Cost,
  non-determinism, and would have required network access on CI.
- **Add a "test mode" to conductor itself.** Upstream churn for a polyphony-
  specific need; out of proportion. Better to keep the testing layer in our
  repo.

## Implementation

- **PR #269** — Python driver, `FakeProvider`, scenario.yaml schema, Pester
  wrapper, first scenario (`close_out_happy_path`).
- **PR #270** — .NET shim binary, `cli_scripts:` extension to scenario
  schema, second scenario (`cascade_remedy_no_stale`) exercising both seams.
- **PR #271** — CI integration: `actions/setup-python@v5` + conductor pip
  install + import-verification step. Scenarios run on every PR build.

## References

- **`tests/harness/README.md`** — user-facing layout and run instructions.
- **`.github/skills/polyphony-harness/SKILL.md`** — agent-loadable companion.
- Issue #272 — upstream conductor `/api/gate` endpoint.
- State-effects catalog — `docs/polyphony-state-effects-catalog.md`
  (Strategy 1 of the four).
- Issues #162, #163, #164 — Strategies 2, 3, 4 (deferred).
