# polyphony conductor workflow registry

Conductor workflow registry for the Polyphony SDLC engine. Co-located with
the polyphony repo so workflow YAMLs, runtime scripts, and the polyphony CLI
evolve together. See `index.yaml` for the manifest of exposed workflows.

Generic, type-agnostic conductor workflow scripts powered by **Polyphony** routing decisions.

These scripts replace the hardcoded type-specific scripts in 	wig-conductor-workflows.
Each script is a thin wrapper that calls Polyphony for routing intelligence.

## Design Principles

- **P5**: Zero type name literals in any script
- **P8**: Deterministic scripts, not agent steps
- Scripts own: exit codes, output format, error messaging, env vars
- All intelligence lives in Polyphony CLI

## Reference

The eference/ directory contains the original type-specific scripts being replaced.

## Prerequisites

- Polyphony CLI (polyphony) must be in PATH
- .conductor/process-config.yaml must exist in the target repo
