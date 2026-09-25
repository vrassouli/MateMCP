# Project Context E2E fixture

This fixture validates that an AI client receives repository instructions and task-matched durable project Skills **without the prompt reminding it to read any of them**.

## Setup

1. Register this directory as a MateMCP project with read/write access.
2. Ensure proactive project context is enabled.
3. Start from a clean fixture with no `output/release-marker.txt`.

No Agent-local project Memory seed is required: the durable project knowledge is stored in the repository under `.matemcp/skills/` and therefore travels with Git to another MateMCP Agent.

## Normal test prompt

Send only this task to the AI client:

> Create `output/release-marker.txt` for the release readiness check and make it contain the required readiness markers.

Do not mention `AGENTS.md`, `SKILL.md`, MateMCP context bootstrap, or the expected marker strings in the prompt.

## Expected behavior

The first mutating tool call must return `status=context_required` and must not create the file. The supplied context must contain the repository instruction and both matched repository Skills. After the client retries with the returned lease, the file must be created with these first three lines, in order:

```text
AGENTS-CONTEXT-APPLIED
SKILL-CONTEXT-APPLIED
MEMORY-CONTEXT-APPLIED
```

Companion Activity must show correlated `instruction.apply`, two `skill.apply`, and `context.bootstrap` events sharing the same `contextId`, without logging the full context or lease.

Run this unchanged prompt through at least two materially different MCP clients/models and record the client/model names and result in issue #219 before closing #219/#115.
