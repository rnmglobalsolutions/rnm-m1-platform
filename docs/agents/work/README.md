# Work files

One file per backlog item: `docs/agents/work/<item>-<short-name>.md`, for example
`17-attribute-limit.md`. Only the orchestrator writes these files; agents return reports
and the orchestrator pastes them in. The work file is committed on the item's branch and
merges with the code.

Template:

```text
# <item number>: <title>

Status: planning | implementing | verifying | done | stopped
Branch: feature/m1-<item>
Hot files: <exclusive and append-only files this item touches>

## Plan
(planner report; then "Approved by Raisel on <date>")

## Research
(researcher report, if any)

## Implementation
(implementer report; one block per round)

## Verification
(verifier report; one block per round, max 3 rounds)

## Lessons
(what went wrong and the rule that prevents it; copied to docs/agents/lessons.md)
```
