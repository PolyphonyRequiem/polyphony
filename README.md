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

# Check environment health and diagnostics
polyphony health
```

### polyphony health

The `polyphony health` command runs a suite of environment diagnostics to verify that your development setup is compatible with Polyphony's requirements. It checks for:

- .NET runtime version and AOT support
- Required environment variables
- Presence and versions of critical dependencies (e.g., twig CLI, YamlDotNet)
- File system permissions for key directories
- Configuration file validity (e.g., `.conductor/process-config.yaml`)

#### Example output

```text
[OK] .NET 10.0.12 (AOT enabled)
[OK] twig CLI found at ~/.twig/bin/twig
[OK] YamlDotNet 13.2.1 available
[OK] .conductor/process-config.yaml present and valid
[WARN] profile.yaml missing (not required, but recommended)
[FAIL] Environment variable POLYPHONY_ENV not set
```

- **[OK]**: All checks passed for this item.
- **[WARN]**: Non-blocking issue; recommended for best experience.
- **[FAIL]**: Blocking issue; must be resolved for Polyphony to function.

#### How to interpret results
- Address all **[FAIL]** items before running workflows.
- Review **[WARN]** items and resolve as appropriate for your project.
- If you encounter issues not covered by `health`, consult the onboarding guide or open an issue.


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
