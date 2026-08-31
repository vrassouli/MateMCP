# Development Workflow

This document describes the default engineering workflow for MateMCP. The mandatory summary is in the repository-root `AGENTS.md`.

## Bug reports

When a user reports a defect:

1. Search existing Issues for the same problem.
2. If none clearly matches, create a new Issue before changing code.
3. Record:
   - actual behavior;
   - expected behavior;
   - reproduction steps or observed sequence when known;
   - affected component(s), such as Agent, Relay, API/Control Plane, installer, or dashboard;
   - relevant logs or error messages without secrets.
4. Investigate the root cause and update the Issue if the understanding materially changes.

## Implementing a fix

Keep fixes focused on the root cause and avoid unrelated cleanup unless it is necessary for correctness.

For small and isolated changes, direct commits to `main` are acceptable when current repository practice allows it. Use a dedicated branch and PR when a change is risky, architectural, spans multiple components, requires review, or would benefit from a reviewable migration path.

Reference the Issue in commits/PRs. Prefer `Refs #<issue>` until validation is complete. `Fixes #<issue>` is appropriate when the linked change is expected to fully resolve the defect and the merge/commit semantics are intentional.

## Validation

Validation should match the affected surface. Examples:

- Agent code: build/test the Agent and verify packaging/install workflows when relevant.
- Relay code: build/test Relay, confirm Relay image publication, and exercise an Agent-to-Relay connection when practical.
- API/Control Plane code: build/test API, confirm API image publication, and exercise affected endpoints or dashboard behavior.
- Cross-component protocol changes: verify both sides together rather than validating each side only in isolation.
- Installer/update changes: test the actual one-line install/update path on the affected platform when practical.

A green local build is not a substitute for relevant CI when CI exists.

## Closing Issues

Close a bug Issue only after the evidence supports that it is fixed:

- implementation is committed or merged;
- relevant tests/builds pass;
- required CI is green;
- relevant deploy/package/image output is successfully produced when applicable;
- end-to-end verification is performed when it materially reduces regression risk.

If the code is complete but deployment or verification is still pending, keep the Issue open and note the remaining step.

## Reporting completion

When reporting a completed fix to the user, include the useful evidence:

- Issue number;
- commit or PR;
- CI/build result;
- deployment/image status if relevant;
- any user action required to receive the fix, such as updating Relay, API, or Agent.

Do not say a change is fully deployed if it has only been committed.

## Durable project rules

Rules that should survive across chats, tools, contributors, and AI agents must be stored in the repository. Use:

- `AGENTS.md` for concise mandatory instructions;
- `docs/` for detailed engineering/process documentation;
- code comments/tests/configuration for rules that are best enforced close to the implementation.

When the user establishes a new durable rule, update these repository files instead of relying only on conversation context.
