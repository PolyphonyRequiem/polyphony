# AB#2986: Prompt Substitution and Audit

## Audit Summary

- All runtime logic in Polyphony (C# and PowerShell) uses validator-driven state names and transitions.
- No hard-coded state names (e.g., "Done", "Doing", "Removed", "Active") exist in runtime logic or workflow YAMLs.
- Any remaining hard-coded state names are limited to:
  - Test code (for fixture setup/validation)
  - Documentation and comments
  - Known deferred-violation script registries (not extended)
- All prompt substitutions for state names are runtime-injected from validator output.

## Validator Coverage

- Tests verify that state transitions and terminal checks are performed via the validator and StateCategoryResolver.
- No test or runtime code relies on hard-coded state names for logic or routing.

---

This audit satisfies the requirements of AB#2986. No code changes were required for runtime logic. All future changes must continue to enforce runtime-injected vocabulary for state names.
