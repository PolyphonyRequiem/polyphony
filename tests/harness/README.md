# Polyphony Workflow Path-Coverage Harness

A scenario-based test harness that runs the **real conductor engine**
against **real workflow YAML** in `.conductor/registry/workflows/`,
substituting only the LLM provider boundary (with a scripted
`FakeProvider`) and the gate boundary (with conductor's `--skip-gates`
auto-pick). The goal: prove that a given combination of agent outputs
walks the route table to the expected terminal — without spending real
LLM tokens, mutating real branches, or touching real ADO.

This document explains the layout, how to run scenarios locally, and
how to add a new scenario.

## Status

**First scenario shipped.** `close_out_happy_path` runs `close-out.yaml`
end-to-end with a single scripted agent and asserts the workflow reaches
`workflow_completed`. Future PRs add the .NET shim binary (for
workflows with `script:` nodes), additional P0 scenarios, and a custom
gate handler for non-default gate selections.

See [`files/harness-design.md`](../../session-state/...) — the full
design — for the long-form rationale and tier-2 (subprocess shim) plan.

## Why a separate Python driver

Conductor is a Python application. Polyphony is .NET. Conductor exposes
a clean `AgentProvider` ABC and accepts a `provider` constructor kwarg
on `WorkflowEngine` — the cheapest way to inject a deterministic LLM
fake is to construct the engine in-process from Python. A separate .NET
shim handles the orthogonal concern of intercepting CLI calls (next PR);
the two layers compose because each fakes a different conductor
boundary.

## Layout

```
tests/harness/
├── README.md                         # this file
├── run-scenarios.Tests.ps1           # Pester wrapper — discovery + assertion
├── driver/
│   ├── run.py                        # CLI entrypoint (python -m driver.run <dir>)
│   ├── scenario.py                   # scenario YAML loader + dataclasses
│   ├── trace.py                      # event recorder + assertion engine
│   └── fakes/
│       └── provider.py               # FakeProvider(AgentProvider)
└── scenarios/
    └── <scenario_name>/
        └── scenario.yaml             # workflow path, agent scripts, expectations
```

## Scenario YAML format

```yaml
workflow: .conductor/registry/workflows/<name>.yaml

inputs:
  key: value                          # passed to engine.run(inputs)

agent_scripts:
  <agent_name>:
    - content:                        # scripted AgentOutput.content
        field: value
    # second invocation — only needed if the agent runs more than once
    - content:
        field: other-value

expected_trace:
  agents_executed:                    # ordered subsequence (not equality)
    - <agent_name>
  reached_terminal: true              # workflow_completed event observed
  output_contains:                    # optional partial workflow.output match
    summary: "All clean"
```

The fake provider raises a clear error if the workflow asks for an agent
the scenario didn't script, or asks for a scripted agent more times than
scripted. Scenarios are intentionally minimal — assertions are
**ordered subsequence**, not exhaustive equality, so adding a new
lifecycle event to a workflow doesn't silently break unrelated
scenarios.

## Running locally

### Prerequisites

- A Python interpreter (3.11+) with `conductor` and `ruamel.yaml`
  importable.
- Pester 5+ on PowerShell 7+.

The simplest local setup is a conductor checkout with its own venv:

```powershell
# One-time
git clone https://github.com/microsoft/conductor C:\path\to\conductor
cd C:\path\to\conductor
python -m venv .venv
.\.venv\Scripts\pip install -e .
.\.venv\Scripts\pip install ruamel.yaml

# Then point the harness at it
$env:HARNESS_PYTHON = 'C:\path\to\conductor\.venv\Scripts\python.exe'
```

### Run all scenarios

```powershell
Invoke-Pester tests/harness/run-scenarios.Tests.ps1
```

### Run one scenario directly

```powershell
$env:PYTHONPATH = 'tests/harness'
& $env:HARNESS_PYTHON -m driver.run tests/harness/scenarios/close_out_happy_path
```

The driver prints a JSON result document to stdout on success
(`{"passed": true, ...}`). Conductor itself writes Rich-formatted
output to stdout too, so when invoked from Pester the driver writes the
JSON to a file via `--output-json` to keep parsing clean.

## Adding a new scenario

1. Create `tests/harness/scenarios/<name>/scenario.yaml`.
2. Set `workflow:` to the production workflow path.
3. List every agent the workflow will execute under `agent_scripts:`,
   each with a scripted `content:` matching the agent's declared
   `output:` schema.
4. Declare `expected_trace.agents_executed` — the ordered subsequence of
   agent names you expect to be visited.
5. Run the scenario directly to iterate. The fake provider's
   `FakeProviderError` messages tell you exactly which agent script is
   missing or stale.

## What this harness does not yet cover

- **Workflows with `script:` nodes that shell out to polyphony, twig, or
  gh.** Those need the .NET subprocess shim (next PR). 12 of the 13
  production workflows have at least one `script:` node, so this is the
  immediate next gap to close.
- **Workflows with non-default human gate routing.** `--skip-gates`
  auto-picks the first option; scenarios that need to walk a different
  branch will need a custom `HumanGateHandler` (deferred — addresses
  the original design's Phase 0 conductor PR).
- **Sub-workflow path coverage.** Sub-workflows are loaded by the same
  engine but with their own provider/registry; the fake propagation
  story for sub-workflows will need verification once we have a
  scenario that exercises one.

## Why this layout (vs. the original design)

The original design (`files/harness-design.md`, 1937 lines) proposed
two tiers — in-process Python harness + .NET subprocess shim — gated on
an upstream conductor PR adding `gate_handler` and `script_executor`
DI kwargs to `WorkflowEngine.__init__`. Re-reading conductor's
`__init__` showed that the **provider** boundary is already injectable
via the existing `provider`/`registry` kwargs. The first useful slice
of the harness — agent-only workflows like `close-out.yaml` — needs no
new conductor seams at all. Shipping the smaller slice first proves the
mechanics and unblocks subsequent work; the conductor PR (and the .NET
shim) can land when scenarios actually require them.
