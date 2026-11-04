# Lessons

Every agent reads this before starting. Only the orchestrator writes it. At most 30
active lessons; see `docs/implementation-plan.md` ("How agents learn from their
mistakes") for when lessons are added, promoted, or removed.

Entry format:

```text
### YYYY-MM-DD: <short title>
What happened: <one or two sentences>
Rule: <what to always or never do from now on>
Applies to: <agents or areas>
Source: <work file or PR>
```

---

### 2026-11-02: In-memory CRM doubles hid a real bug

What happened: booking writes the top-level `LeadStatus` column, while the follow-up
stop condition reads `Attr_leadStatus`. Tests passed because the in-memory CRM doubles
do not store data the way Azure Table Storage does.
Rule: test CRM behavior against the real `AzureTableCrmAdapter` on Azurite, not only
with in-memory doubles.
Applies to: implementer, verifier; follow-up and channels areas.
Source: plan review, backlog items 6 and 17.

### 2026-11-03: Compare branches against origin/main, not the local main

What happened: the orchestrator compared `feature/master-class` with a stale local
`main` and reported it 51 commits ahead and never merged. It had been merged on GitHub
in PR #51.
Rule: run `git fetch` first and compare with `origin/main`.
Applies to: all agents and the orchestrator.
Source: Phase 0, step 2.
