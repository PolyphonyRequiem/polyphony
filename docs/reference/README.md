---
doc_type: index
status: active
synopsis: Index for reference docs — CLI verbs, tag namespace, state effects, conductor directory, process-config schema.
---

# Reference

Stable reference material for polyphony. Diátaxis "reference" quadrant: information-oriented, complete, predictable.

If you are looking for explanation (the "why"), see [`../concepts/`](../concepts/README.md). If you are looking for a step-by-step walkthrough, see [`../guides/`](../guides/README.md).

| Doc | What it covers |
|---|---|
| [`polyphony-cli-reference.md`](polyphony-cli-reference.md) | Every polyphony CLI verb — JSON shapes, exit codes, worked examples — across all ~24 verbs / 9 command groups. |
| [`polyphony-tags.md`](polyphony-tags.md) | The `polyphony:*` tag namespace stamped on every work item the polyphony pipeline owns. Authoritative spec referenced from `src/Polyphony/Tagging/`. |
| [`polyphony-state-effects-catalog.md`](polyphony-state-effects-catalog.md) | Living catalog of what each polyphony verb writes (tags, fields, state). Bootstrapping — not a completeness guarantee. |
| [`polyphony-conductor-directory.md`](polyphony-conductor-directory.md) | Every file in `.polyphony-config/` outside `process-config.yaml` — per-type defs, agent guidance, profile. |
| [`polyphony-process-config-schema.md`](polyphony-process-config-schema.md) | Full `process-config.yaml` schema with V-1..V-14 validation rules and per-template worked examples (Basic / Agile / Scrum / CMMI). |

The [`../glossary.md`](../glossary.md) is also reference material but lives at the docs root because it is referenced from everywhere.
