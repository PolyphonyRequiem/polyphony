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
# Run environment and configuration diagnostics
polyphony health

# Determine routing for a work item
polyphony route --work-item 1234 --config .conductor/process-config.yaml

# Validate a state transition
polyphony validate --work-item 1234 --event begin_planning

# Display hierarchy with role annotations
polyphony hierarchy --work-item 1234 --depth 3
```

## Commands Reference

| Command                | Description                                                      |
|-----------------------|------------------------------------------------------------------|
| `polyphony health`    | Run environment and configuration diagnostics.                    |
| `polyphony route`     | Determine routing for a work item.                                |
| `polyphony validate`  | Validate a state transition for a work item.                      |
| `polyphony hierarchy` | Display work item hierarchy with role annotations.                |

## Health Command

The `polyphony health` command runs a suite of diagnostics to verify your environment and configuration. It checks for:

- Presence and validity of `.conductor/process-config.yaml`
- Availability of required tools (`twig`, `git`)
- OS, architecture, .NET, and Polyphony version

**Sample output:**

```json
{
  "checks": [
    { "name": "process-config", "success": true, "message": "Loaded successfully" },
    { "name": "twig", "success": true, "message": "Found on PATH: /usr/local/bin/twig" },
    { "name": "git", "success": true, "message": "Found on PATH: /usr/bin/git" }
  ],
  "os": "Windows_NT",
  "architecture": "x64",
  "dotnetVersion": "10.0.0",
  "polyphonyVersion": "1.2.3"
}
```

### Interpreting Results

- If any check fails, review the `message` for remediation steps (e.g., missing config, tool not found, invalid YAML).
- All fields are always present; no nulls.
- Exit code 0 means all checks passed; exit code 4 means one or more critical health checks failed.
- Use this command after setup or when troubleshooting environment issues.
- For failed checks, follow the remediation steps in the `message` field. Common actions:
  - Reinstall missing tools (`twig`, `git`)
  - Fix or restore `.conductor/process-config.yaml`
  - Ensure your PATH includes required binaries
  - Re-run `polyphony health` after making changes
- If you are unable to resolve an issue, copy the full output and seek help in the project support channel.

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
