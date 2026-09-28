---
name: api-sqlite
description: Keep MateMCP API code compatible with the repository's SQLite regression path and current EnsureCreated schema flow.
---

# MateMCP API SQLite compatibility

Use this skill when changing MateMCP.Api persistence queries or tests.

## Rules

- The API currently supports SQLite and creates its schema with `EnsureCreated`; do not introduce migration-only assumptions without an explicit schema/deployment plan.
- EF Core's SQLite provider may not translate `DateTimeOffset` comparisons or ordering in otherwise valid LINQ queries, especially through navigation joins.
- Keep filtering server-side for translatable keys such as owner IDs and status, then materialize a bounded result before applying unsupported `DateTimeOffset` comparison/order logic in memory.
- Do not fall back to unbounded client evaluation for large datasets; constrain by ownership/status or another selective predicate first.
- Add or retain SQLite integration coverage for persistence-query changes so provider-specific translation failures surface before delivery.
