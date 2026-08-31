# MateMCP Agent Instructions

These rules are mandatory for AI-assisted work in this repository unless the user explicitly overrides them for a specific task.

## Issue-first workflow

- Every user-reported bug or defect must have a GitHub Issue before implementation starts.
- Reuse an existing Issue only when it clearly tracks the same defect. Do not repurpose unrelated Issues.
- The Issue should capture the observed behavior, expected behavior, affected component(s), and any useful reproduction details.
- Reference the Issue from the implementation commit or PR using `Refs #<issue>` while work is in progress.
- Use `Fixes #<issue>` only when the change is actually expected to resolve the Issue.

## Completion criteria

Do not consider a bug fixed merely because code was changed.

Before closing a bug Issue:

1. The implementation must be committed.
2. Relevant build/tests must pass.
3. Relevant GitHub Actions/CI must be green when such workflows exist.
4. Deployment/image publication must be verified for deploy-related changes.
5. When practical and materially useful, perform a real end-to-end verification against the affected Agent/API/Relay path.
6. Only then close the Issue as completed.

If verification is incomplete, leave the Issue open and state what remains.

## Change discipline

- Prefer the smallest correct change that addresses the root cause.
- Add regression coverage for important bugs when practical.
- Do not modify unrelated Issues, files, or configuration as part of a fix.
- Never overwrite existing Issue bodies or comments for unrelated work.
- Keep security-sensitive behavior explicit and auditable.
- Do not expose credentials, tokens, secrets, or private keys in logs, commits, Issues, or tool output.

## Branches and pull requests

- Small, isolated fixes may be committed directly to `main` when repository practice permits it.
- Larger, risky, cross-cutting, or architectural changes should use a dedicated branch and PR.
- PRs should reference the tracking Issue and summarize validation performed.

## CI and delivery

- After a code change, inspect the workflows relevant to the changed component.
- Do not report delivery as complete while required CI is failing or still unresolved.
- For Relay/API/container changes, verify that the corresponding image build/publish workflow succeeds before calling the change deployable.
- For Agent packaging/install changes, verify the relevant platform packaging workflow and installation path.

## Repository memory

- Durable project rules belong in this repository, primarily in `AGENTS.md` and supporting files under `docs/`.
- Do not rely solely on conversation memory or external agent context for workflow rules that future contributors or agents need to follow.
- When a new durable engineering rule is agreed with the user, update the repository documentation so future AI agents and human contributors can discover it.

See `docs/development-workflow.md` for the detailed bug-fix and delivery workflow.
