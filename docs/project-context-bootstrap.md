# Project Context Bootstrap

MateMCP must not rely on an AI client remembering to inspect repository-local instructions before changing a project.

For project-scoped mutating tools, the Agent performs a context preflight before the mutation. When repository instructions or relevant durable context exist, the first call returns `status = context_required` and does not execute the requested mutation.

The response contains:

- the registered project identity;
- applicable repository instruction sources such as root/nested `AGENTS.md`;
- bounded relevant Skills & Memory when available;
- a context hash;
- a short-lived opaque `contextLease`.

The client must read the supplied context and retry the same operation with the lease. The Agent accepts the lease only for the same project and instruction hash. Changing an applicable `AGENTS.md` invalidates the previous lease and forces context to be surfaced again.

Current explicit user instructions and MateMCP host/security policy take precedence over persisted repository or memory context.

## Initial tool coverage

The initial implementation covers:

- `shell_exec` before a project-scoped process is started;
- `shell_session_start` before a project-scoped PTY/ConPTY process is started;
- `filesystem_write` before directories/files are created or changed.

Read-only filesystem operations do not require a lease. Interactive session writes are covered by the preflight performed when the project-scoped session is started.

Further Skills/Memory matching, additional mutating surfaces, Companion observability, and cross-client E2E validation are tracked under #115 and its follow-up issues.
