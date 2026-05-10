---
name: polyphony-harness
description: >-
  Activate when authoring, modifying, debugging, or running scenarios in the
  polyphony workflow path-coverage harness under `tests/harness/`. The harness
  drives the real conductor engine in-process from Python with a scripted
  `FakeProvider` for the LLM boundary and a .NET shim binary on PATH for
  `script:` nodes. Use this when asked to "write a scenario", "test workflow X
  end-to-end without real LLMs/ADO", "reproduce dogfood incident Y in CI", or
  any task that touches `tests/harness/scenarios/`, the Python driver under
  `tests/harness/driver/`, or the shim under `tests/harness/shim/`.
user-invokable: false
---

# Polyphony Workflow Path-Coverage Harness Skill

Companion skill to **`tests/harness/README.md`** (layout + usage) and
**`docs/decisions/harness-mvp.md`** (design rationale). Read this when you are
*acting on* the harness; read those when you are *deciding* about it.

For changes inside `src/Polyphony/Commands/` use `polyphony-cli-developer`. For
workflow YAML changes use `polyphony-workflow-author`. This skill covers
**testing those workflows** without a real LLM, real ADO, or real git
mutations.

---

## What the harness does (one paragraph)

A scenario is a directory under `tests/harness/scenarios/<name>/` containing a
single `scenario.yaml` that names a workflow under
`.conductor/registry/workflows/`, supplies the agent-output script the
`FakeProvider` should replay verbatim, optionally declares CLI scripts the
shim should intercept, and asserts the workflow reaches an expected terminal.
The Python driver under `tests/harness/driver/` constructs a `WorkflowEngine`
in-process with the fake provider and runs it with `--skip-gates`. The
`tests/harness/run-scenarios.Tests.ps1` Pester wrapper discovers scenarios at
test time and runs them on every CI build.

**Three seams are faked:**

1. **LLM seam** — `FakeProvider` returns scripted strings; never calls a real model.
2. **CLI seam** — the .NET shim binary is staged on PATH under names
   `polyphony`, `twig`, `gh` and matches argv against a scenario manifest.
3. **Gate seam** — `--skip-gates` auto-picks the first option of every human
   gate. Non-default gate routing is **not yet supported** (deferred —
   addresses the conductor `/api/gate` upstream PR, issue #272).

---

## Where files live

```text
tests/harness/
├── README.md                 # User-facing layout + run instructions
├── run-scenarios.Tests.ps1   # Pester wrapper — auto-discovers scenarios
├── driver/                   # Python harness driver
│   ├── run.py                # CLI entry: python -m driver.run <scenario-dir>
│   ├── scenario.py           # scenario.yaml parser
│   ├── fake_provider.py      # conductor AgentProvider implementation
│   └── shim_runtime.py       # .NET shim build + PATH stage + audit reader
├── shim/                     # .NET shim binary intercepting `script:` calls
│   ├── Polyphony.HarnessShim/        # The binary
│   └── Polyphony.HarnessShim.Tests/  # xUnit tests for matcher/manifest
└── scenarios/
    ├── close_out_happy_path/scenario.yaml      # Agent-only
    └── cascade_remedy_no_stale/scenario.yaml   # Agent + script seams
```

---

## Adding a new scenario — the canonical template

```yaml
# tests/harness/scenarios/<your_scenario_name>/scenario.yaml
workflow: .conductor/registry/workflows/<workflow>.yaml
expected_terminal: workflow_completed   # or another terminal node id

# Required: scripts the FakeProvider plays back, in conductor's call order.
agent_outputs:
  - agent: agent_id_as_declared_in_workflow
    output: |
      {"some_field": "value"}
  - agent: another_agent
    output: |
      Some unstructured text the workflow's regex parser will match.

# OPTIONAL: only required when the workflow contains `script:` nodes.
cli_scripts:
  - command: polyphony           # one of polyphony / twig / gh
    args: [state, next-ready]    # prefix match against actual argv[1:]
    stdout: '{"status":"ready_for_review"}'
    exit_code: 0
```

### How the matcher works

- Order matters within `agent_outputs:` — the FakeProvider replays them in
  the order the workflow calls each agent. If the workflow calls
  `agent_id_as_declared_in_workflow` twice, list it twice.
- `cli_scripts:` matching is **first-match-wins**: the shim walks the list
  and picks the first entry whose `command` equals argv[0]'s basename AND
  whose `args` is a prefix of the actual argv[1:].
- Unmatched script calls exit 99 with structured stderr — the workflow
  typically routes that as a failure.
- Manifest errors exit 98.

---

## Running scenarios

```powershell
# Locally, single scenario, verbose
$env:HARNESS_PYTHON = 'C:\path\to\conductor\.venv\Scripts\python.exe'
python -m driver.run tests/harness/scenarios/close_out_happy_path --verbose

# Locally, all scenarios via Pester
Invoke-Pester tests/harness/run-scenarios.Tests.ps1 -Output Detailed

# CI: scenarios run automatically as part of `Test (Pester)` step in ci.yml.
```

If `HARNESS_PYTHON` is unset and the well-known dev path
(`C:\Users\dangreen\projects\conductor\.venv`) doesn't exist, the wrapper
falls back to `python` on PATH. CI's `setup-python` step provides this.

---

## What CI gives you for free

- Python 3.12 + `ruamel.yaml` + conductor (from `git+https://github.com/microsoft/conductor.git@main`).
- .NET 11 SDK (already there for the main build).
- The shim binary builds on demand into `tests/harness/shim/Polyphony.HarnessShim/bin/Release/<rid>/...` and is cached by file mtime.
- Pester picks up `tests/harness/run-scenarios.Tests.ps1` automatically.

If you add a scenario, **you don't need to register it anywhere** — Pester
discovers it via filesystem scan at test-discovery time.

---

## Common pitfalls

### Helper functions in the Pester wrapper

Helper functions defined at top level of `run-scenarios.Tests.ps1` are
**not visible inside `BeforeAll`** — Pester's scope rules. The prereq
probe (the one that decides whether to skip-with-diagnostic vs. run) must
be inlined inside `BeforeAll`. Don't refactor it out without testing the
skip-mode path.

### Self-contained publish required for the shim

`Polyphony.HarnessShim.csproj` publishes **self-contained single-file**
per RID. Framework-dependent publish creates an apphost shim that needs
the adjacent `.dll` — copying the binary under three names
(`polyphony`, `twig`, `gh`) breaks framework-dependent because the
renamed copies have no adjacent `.dll`. Don't switch this back.

### Cross-platform path handling in shim tests

`Path.GetFileNameWithoutExtension(@"C:\bin\twig.exe")` on Linux returns
`"C:\bin\twig"` because backslash isn't a path separator. Tests must use
`Path.DirectorySeparatorChar` for any path string that needs to mean the
same thing on both platforms. Bit me on PR #270's first CI run.

### conductor `script:` invocation contract

Conductor uses `asyncio.create_subprocess_exec(rendered_command, *rendered_args, env={...})`.
**No shell.** PATH lookup applies. Stdout is parsed as JSON and merged
into `<step>.output.<key>` (also kept available as `<step>.output.stdout`).
`script_completed` fires regardless of exit code; non-zero doesn't auto-fail
— it's up to route conditions. The shim must produce JSON when the step's
downstream consumer expects fields, otherwise the workflow's Jinja
references will fail at render time.

### --skip-gates auto-picks the first option

If your scenario needs to walk a non-default gate branch, you cannot do
it today with the harness as shipped. This is the single largest known
gap. Tracked under issue #272 (upstream conductor `/api/gate` endpoint).
For now, design scenarios to exercise the first-option branch only.

---

## What scenarios are NOT for

- **Performance testing.** The harness is path-coverage, not load.
- **Real ADO state assertions.** Nothing touches ADO; assertions are
  about the workflow's terminal node and the audit log of CLI calls.
- **Sub-workflow path coverage.** Sub-workflows load with their own
  provider/registry; the fake propagation story for them needs
  verification once we have a scenario that exercises one.
- **Validating that the polyphony CLI itself works.** That's the
  xUnit suite under `tests/Polyphony.Tests/`. Use the shim only to
  intercept calls the workflow makes; don't try to assert on the
  CLI's actual JSON contract from inside a scenario.

---

## When a scenario fails on CI

1. Read the Pester failure: it includes the scenario name and the
   driver's structured failure JSON (terminal mismatch, unmatched
   script call, unscripted agent call, exception in workflow).
2. The driver prints a per-step audit when run with `--verbose`. Re-run
   it locally with the same scenario dir to get the full trace.
3. If the failure is "unmatched script call", check whether a workflow
   change added a new `script:` node that needs a `cli_scripts:` entry,
   or whether the existing matcher entry's `args` prefix is no longer a
   prefix of the actual argv.
4. If the failure is "unscripted agent call", check whether a workflow
   change added an agent that needs an `agent_outputs:` entry, or
   whether agent ordering shifted.

---

## Cross-references

- **`tests/harness/README.md`** — user-facing run instructions, layout.
- **`docs/decisions/harness-mvp.md`** — design rationale (why in-process
  Python, why shim on PATH, why first-match-wins, why `--skip-gates`).
- **PR #269** — driver + first scenario.
- **PR #270** — .NET shim binary + first script-seam scenario.
- **PR #271** — CI integration; scenarios run on every PR build.
- **Issue #272** — upstream conductor `/api/gate` endpoint (unblocks
  non-default gate routing).
