# Architecture decision records

Each record follows the format of Michael Nygard: context, decision, consequences, and the alternatives that
were considered. All records were accepted for v1.0.0 (October 2026).

| ADR | Title |
| --- | --- |
| [0001](0001-minimal-apis-with-typed-results.md) | Minimal APIs with typed results |
| [0002](0002-jsonb-instead-of-eav.md) | Store schemas and submissions as `jsonb`, not as entity-attribute-value rows |
| [0003](0003-immutable-published-versions.md) | Published versions are immutable; submissions are pinned to a version |
| [0004](0004-own-schema-format-instead-of-json-schema.md) | An own schema format and validation engine instead of JSON Schema |
| [0005](0005-linear-time-regular-expressions.md) | Linear-time regular expressions against ReDoS |
| [0006](0006-autosave-never-rejects-and-uses-etags.md) | Autosave never rejects input and uses ETags for concurrency |
| [0007](0007-breaking-change-gate-on-publish.md) | Publishing a breaking change needs explicit confirmation |
| [0008](0008-integration-tests-with-testcontainers.md) | Integration tests against real PostgreSQL with Testcontainers |
| [0009](0009-uuidv7-keys-and-keyset-paging.md) | UUIDv7 keys and keyset paging |
