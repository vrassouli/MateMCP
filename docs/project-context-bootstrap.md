# Project Context Bootstrap

MateMCP must not rely on an AI client remembering to inspect repository-local instructions before changing a project.

For project-scoped mutating tools, the Agent performs a context preflight before the mutation. When repository instructions, applicable repository Skills, or relevant global durable Memory exist, the first call returns `status = context_required` and does not execute the requested mutation.

The response contains:

- the registered project identity;
- applicable repository instruction sources such as root/nested `AGENTS.md`;
- bounded task-matched repository Skills;
- bounded relevant **global** Skills & Memory entries when available;
- a context hash;
- a short-lived opaque `contextLease`.

The client must read the supplied context and retry the same operation with the lease. The Agent accepts the lease only for the same project and applicable context hash. Changing an applicable `AGENTS.md`, matched repository Skill, or relevant global durable Memory changes the hash, invalidates the previous lease, and forces the updated context to be surfaced again.

Current explicit user instructions and MateMCP host/security policy take precedence over persisted repository Skill or global Memory context. Project-specific durable knowledge must be committed to the repository rather than stored in the Agent-local Memory store.

## Project Skill discovery

MateMCP discovers `SKILL.md` documents only from explicit project conventions instead of crawling arbitrary files. Supported locations are:

- `/SKILL.md`;
- `/.matemcp/skills/**/SKILL.md`;
- `/.agents/skills/**/SKILL.md`;
- `/.agent/skills/**/SKILL.md`;
- `/skills/**/SKILL.md`.

A Skill can be plain Markdown. MateMCP derives useful matching terms from its heading, text, and path. Optional lightweight front matter can make activation more deterministic:

```markdown
---
name: Release workflow
description: Package and publish releases
triggers: [release, publish, package]
mode: required
---
# Release workflow
...
```

Recognized metadata includes `name`/`title`, `description`, `triggers`/`tags`/`keywords`, and `mode`/`activation`. `mode: required` or `mode: always` makes a Skill active without a lexical task match. Other Skills are ranked against the current command/task/path and only the highest relevant bounded set is supplied.

Global durable Memory entries also require task relevance. Durable `rule` entries, or entries tagged `always`/`required`, remain automatically active. Project-specific decisions, procedures, conventions, and reusable lessons should be added or updated as repository `SKILL.md` files—preferably under `.matemcp/skills/<name>/SKILL.md`—so they are versioned and move with the repository to another MateMCP Agent.

## Global Memory vs repository Skills

MateMCP deliberately has no active project-scoped Memory store. The split is:

- **Global Skills & Memory**: cross-project preferences, rules, and reusable procedures stored by the local Agent.
- **Project knowledge**: `SKILL.md` files committed with the repository.

When upgrading from an older Agent, legacy project-scoped entries are archived locally and, when their registered project is available and writable, migrated to `.matemcp/skills/<name>/SKILL.md`. The active local store is then global-only. The archive remains as a recovery path for entries whose repository was unavailable during migration.

## Initial tool coverage

The initial implementation covers:

- `shell_exec` before a project-scoped process is started;
- `shell_session_start` before a project-scoped PTY/ConPTY process is started;
- `filesystem_write` before directories/files are created or changed.

Read-only filesystem operations do not require a lease. Interactive session writes are covered by the preflight performed when the project-scoped session is started.

Additional mutating surfaces, Companion observability, durable write-back guidance, and cross-client E2E validation remain tracked under #115 and its follow-up issues.
