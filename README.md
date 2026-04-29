# Polyphony

Deterministic routing engine for hierarchical SDLC workflows.

## What

Polyphony is a .NET 10 AOT-compiled CLI that provides deterministic routing decisions
for conductor SDLC workflows. Given a work item at any hierarchy depth, it determines:

- **Phase** — Current SDLC phase (needs_planning, needs_seeding, ready_for_implementation, etc.)
- **Action** — Next action to take (plan, seed, decompose, implement, review, close)
- **Validation** — Whether a state transition is legal given ADO process rules and SDLC preconditions

## Why

Conductor workflow scripts previously hardcoded type assumptions (Epic → Issue → Task).
Polyphony decouples routing logic from specific ADO process templates, enabling the same
workflows to work across Basic, Agile, Scrum, CMMI, and custom processes.

## Usage

```bash
# Determine routing for a work item
polyphony route --work-item 1234 --config .conductor/process-config.yaml

# Validate a state transition
polyphony validate --work-item 1234 --event begin_planning

# Display hierarchy with role annotations
polyphony hierarchy --work-item 1234 --depth 3
```

## Building

```bash
dotnet build
dotnet test
./publish-local.ps1  # Deploys to ~/.twig/bin/
```

## Architecture

- References `Twig.Domain` and `Twig.Infrastructure` for work item models and SQLite cache access
- Reads `.conductor/process-config.yaml` for type capabilities and transition mappings
- Outputs structured JSON to stdout; uses exit codes for conductor routing
- Fully deterministic — no AI/LLM in routing decisions
