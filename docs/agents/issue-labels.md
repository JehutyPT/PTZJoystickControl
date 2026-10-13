# Internal Issue Labels

These labels classify repository-owned work and gate implementation. They are separate from optional external-triage labels.

| Canonical role | Tracker label | Meaning |
| --- | --- | --- |
| `prd` | `prd` | Parent product requirements document; never implementation-ready |
| `bug` | `bug` | Internal implementation work that repairs behavior |
| `enhancement` | `enhancement` | Internal implementation work that adds or changes behavior |
| `ready-for-agent` | `ready-for-agent` | Open implementation contract validated for execution |

The repo also uses context labels that never gate readiness: `severity: critical|high|medium|low`, `live-safety`, `phase 1: hardening`, `phase 2: companion`, `phase 3: skaarhoj`, and `chore` (maintenance work; treat as `enhancement` for the one-category rule).

Apply exactly one internal category (`bug` or `enhancement`) to each implementation ticket. Create decomposition tickets category-only, then apply `ready-for-agent` last only when the ticket is open, has zero open formal blockers, and its canonical `## Implementation Contract` section is complete and validated. Never apply `ready-for-agent` to a PRD or provisional contract.
