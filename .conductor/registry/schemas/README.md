# Workflow Payload Schemas

This directory contains payload schemas that define the contract between
workflow producers (e.g., the architect agent) and workflow consumers
(e.g., sub-workflows).

## `research-needs`

**Schema:** [`research-needs.schema.yaml`](research-needs.schema.yaml)
**Example:** [`research-needs.example.yaml`](research-needs.example.yaml)

The `research_needs` payload is the stable contract between the architect
(emitter) and the research sub-workflow (consumer). It was defined in
AB#3133 and is consumed unchanged by AB#3136.

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `topics` | `string[]` | ✅ | Research questions or areas (non-empty) |
| `context` | `object` | ✅ | Originating work item ID + plan excerpt |
| `context.work_item_id` | `integer` | ✅ | ADO work item ID |
| `context.plan_excerpt` | `string` | — | Relevant plan excerpt |
| `budget_hint` | `string` | ✅ | `cheap` or `extended` |
| `archive_scope` | `object` | — | Path/tag constraints for archive lookup |
| `archive_scope.paths` | `string[]` | — | File path prefixes to search |
| `archive_scope.tags` | `string[]` | — | ADO tags to filter work items |

### Budget hints

- **`cheap`** — Archive-only lookup using a fast model. First pass (AB#3133).
- **`extended`** — Live tools + deep-researcher escalation. Reserved for
  future work (AB#3134 / AB#3135).
