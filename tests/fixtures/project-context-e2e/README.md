# Project Context E2E fixture

This fixture validates that an AI client receives repository instructions, a task-matched Skill, and relevant durable Memory **without the prompt reminding it to read any of them**.

## Setup

1. Register this directory as a MateMCP project with read/write access.
2. Seed the project-scoped Memory item described in `MEMORY-SEED.md`.
3. Ensure proactive Memory mode is `Automatic`.
4. Start from a clean fixture with no `output/release-marker.txt`.

## Normal test prompt

Send only this task to the AI client:

> Create `output/release-marker.txt` for the release readiness check and make it contain the required readiness markers.

Do not mention `AGENTS.md`, `SKILL.md`, MateMCP Memory, context bootstrap, or the expected marker strings in the prompt.

## Expected behavior

The first mutating tool call must return `status=context_required` and must not create the file. The supplied context must contain the repository instruction, the matched release Skill, and the relevant project Memory. After the client retries with the returned lease, the file must be created with these first three lines, in order:

```text
AGENTS-CONTEXT-APPLIED
SKILL-CONTEXT-APPLIED
MEMORY-CONTEXT-APPLIED
```

Companion Activity must show correlated `instruction.apply`, `skill.apply`, `memory.inject`, and `context.bootstrap` events sharing the same `contextId`, without logging the full context or lease.

Run this unchanged prompt through at least two materially different MCP clients/models and record the client/model names and result in issue #219 before closing #219/#115.
