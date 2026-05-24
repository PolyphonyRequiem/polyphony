---
doc_type: index
status: active
synopsis: Index of forward-looking proposals not yet ratified — the AB#3253 epic family on typed contracts.
---

# Proposals

Forward-looking proposals not yet ratified. Once accepted, a proposal typically migrates to [`../decisions/`](../decisions/README.md) or is superseded by an implementation plan in [`../projects/`](../projects/README.md).

| Proposal | Status | Work item | Synopsis |
|---|---|---|---|
| [`typed-contract-surface.md`](typed-contract-surface.md) | draft | AB#3255 | Publish a typed contract surface + schemas for polyphony verbs and workflow nodes. Load-bearing for the sibling proposals. |
| [`pr-branch-verb-matrix-collapse.md`](pr-branch-verb-matrix-collapse.md) | draft | AB#3256 | Collapse the PR/branch verb matrix into a smaller, typed surface. Depends on AB#3255. |
| [`script-to-verb-migration.md`](script-to-verb-migration.md) | draft | AB#3258 | Migrate PowerShell helper scripts into typed polyphony CLI verbs. Depends on AB#3255. |

All three are siblings of the AB#3253 epic (the polyphony architecture rationalization). Sibling work items already shipped: AB#3259 (vocabulary normalization — closed via PR #514). AB#3254 (action journal) and AB#3257 (failure-mode gate elimination) are tracked separately.
