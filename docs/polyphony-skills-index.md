# Polyphony Docs & Skills — Reading Index

A landing page for fresh agents and humans landing in the polyphony repo with
no context. This index does **not** explain polyphony itself — it tells you
which document to read first based on what you are about to do.

## What polyphony is, in two sentences

Polyphony is an AOT-compiled .NET CLI that decides *what should happen* to a
work item in an SDLC pipeline — phase, transition, branch hint — based on a
per-repo `.conductor/process-config.yaml`. It pairs with `twig` (which
*executes* the decisions against ADO) and with `conductor` (which orchestrates
the surrounding AI workflows); polyphony itself never writes to ADO.

## If you are about to … read these in order

### 1 · Bootstrap a fresh repo for polyphony
You have twig installed and the polyphony CLI on PATH. The repo has no
`.conductor/` yet.

1. **`polyphony-bootstrap.skill.md`** — the step-by-step (prereqs → directory
   → `process-config.yaml` → `validate-config` → 6-step smoke test → workflow
   wiring → pitfalls).
2. **`polyphony-conductor-directory.md`** — what every file in `.conductor/`
   is for; minimum-viable vs. complete checklists.
3. **`polyphony-process-config-schema.md`** — full YAML schema, V-1..V-14
   rules, worked examples per ADO process template (Basic / Agile / Scrum /
   CMMI / custom).
4. **`polyphony-agent-failure-modes.md`** — the six failure modes a
   prior agent hit; § 6 covers the `scope_removed: Removed` latent bug.

### 2 · Author or modify a workflow YAML or PowerShell helper
You are touching `workflows/*.yaml` or `scripts/*.ps1` (in the twig repo —
see § "Not covered" below for where they live).

1. **`polyphony-workflow-author.skill.md`** — the shell-out idiom, decision
   matrix for which CLI to call, the three-vocabulary rule, canonical helper
   scripts, conventions for adding new ones.
2. **`polyphony-cli-reference.md`** — JSON shapes, exit codes, and worked
   examples for `route` / `validate` / `validate-config` / `hierarchy`.
3. **`polyphony-architecture.md`** *(skim)* — layering and the platform
   abstraction; orient yourself before adding new shell-out sites.
4. **`polyphony-agent-failure-modes.md`** — § 1, § 2, § 3 are the
   workflow-author failures; read before estimating any change.

### 3 · Add or modify a polyphony CLI verb
You are touching `src/Polyphony/Commands/`.

1. **`polyphony-cli-developer.skill.md`** — ConsoleAppFramework command
   pattern, primary-constructor DI, AOT JSON via `PolyphonyJsonContext`,
   `CommandTestBase` scaffolding, `JsonOutputContractTests` checklist.
2. **`polyphony-cli-reference.md`** — confirm the verb you are about to add
   doesn't already exist (the four current verbs cover most "obvious" needs).
3. **`polyphony-architecture.md`** — confirm your new verb belongs in the
   polyphony layer, not in twig or in a helper script.

### 4 · Just understand what polyphony is
You are not changing anything; you want the model.

1. **`polyphony-architecture.md`** — layering, three-vocabulary contract,
   data flow worked example.
2. **`polyphony-cli-reference.md`** — the four verbs, what they each return.
3. **`polyphony-agent-failure-modes.md`** — calibrate your model
   against six concrete failures the docs prevent.

## What this index does NOT cover

- **The `twig` CLI** (set / state / sync / note / show / new / patch / …).
  Use the **twig-cli** skill or the twig repo at
  `C:\Users\dangreen\projects\twig2\`.
- **The `conductor` workflow engine** — YAML schema, routing semantics,
  re-entry, human gates. Use the **conductor** skill.
- **The v2 SDLC workflow suite** — apex `twig-sdlc-v2-full.yaml`, the
  9-workflow tree, recursion budget, parallel PG execution. Use the
  **polyphony-sdlc** skill (`.github/skills/polyphony-sdlc/SKILL.md`).
  The workflow YAMLs themselves live in
  `C:\Users\dangreen\projects\twig2\workflows\`, not in this repo.
- **Per-repo agent guidance** (`agent-guidance/architect.md` etc.) — those
  are tuning files for downstream agents, not polyphony documentation.

## All 9 docs at a glance

| Doc / skill                                  | Size | Audience                       | One-line purpose                                                       |
|----------------------------------------------|-----:|--------------------------------|------------------------------------------------------------------------|
| `polyphony-skills-index.md`                  |  ~6K | Anyone landing fresh           | This file. Which doc to read first based on what you're about to do.   |
| `polyphony-bootstrap.skill.md`               | ~18K | Repo onboarder                 | How to wire a repo from zero `.conductor/` to running workflows.       |
| `polyphony-conductor-directory.md`           | ~15K | Repo onboarder / config author | Reference for every file inside `.conductor/` other than the schema.   |
| `polyphony-process-config-schema.md`         | ~17K | Config author                  | Full YAML schema, V-1..V-14, per-template worked examples.             |
| `polyphony-cli-reference.md`                 | ~15K | Workflow author / CLI consumer | The four verbs, JSON shapes, exit codes, worked examples.              |
| `polyphony-architecture.md`                  | ~18K | Anyone changing layers         | Layering, platform abstraction, three-vocabulary contract, data flow.  |
| `polyphony-workflow-author.skill.md`         | ~11K | Workflow / script author       | Shell-out idiom, decision matrix, canonical helpers, anti-patterns.    |
| `polyphony-cli-developer.skill.md`           | ~13K | Polyphony CLI contributor      | How to add / modify a verb in `src/Polyphony/Commands/`.               |
| `polyphony-agent-failure-modes.md`   |  ~9K | Anyone (calibration)           | Postmortem of 6 failure modes; cite which doc would have prevented each.|

Sizes rounded; see file system for canonical bytes. Files marked
`.skill.md` carry frontmatter and are intended to be auto-loaded by an agent
runtime; the others are reference Markdown.
