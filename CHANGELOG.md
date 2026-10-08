# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-10-08

### Added

- Form definitions with nine field types, constraints, `visibleWhen`/`requiredWhen` conditions
  (`all`/`any`), and cross-field rules.
- Schema checks on save: keys, options, constraint applicability, condition references and value types,
  cycle detection, nesting depth, safe patterns, and rules.
- Validation engine that reports every error with a stable code and returns normalised data. Hidden fields are
  ignored and removed.
- Schema versioning: one draft per form (enforced by a partial unique index), immutable published versions,
  and submissions pinned to their version.
- Compatibility analysis between versions. Publishing a breaking draft needs `acceptBreakingChanges=true`.
- Autosave drafts that never reject input, with optimistic concurrency (`xmin` as ETag, `If-Match`), atomic
  submit, 30-day expiry and background cleanup.
- Submissions stored as `jsonb` with a GIN index; containment filter and keyset paging on UUIDv7 keys.
- OpenAPI 3.1 document and Scalar UI, RFC 9457 problem details, liveness and readiness health checks.
- Input hardening: a 1 MiB request body limit, rejection of JSON that PostgreSQL cannot store (NUL, lone
  surrogates, out-of-range numbers), size caps on options, rules and `in` values, and an `xmin` concurrency
  token on form versions so a stale draft write cannot overwrite a published schema.
- Chiseled, non-root container image; Docker Compose setup; smoke test script.
- 109 unit tests and 52 integration tests against PostgreSQL 16 with Testcontainers.
- GitHub Actions: format check, build, tests with coverage, container smoke test, migration check, Markdown
  lint and link check; image release to GHCR on tags; Dependabot.
- Documentation in English and German: README, architecture, testing; nine ADRs.

[1.0.0]: https://github.com/AhadPorkar/forms-api-dotnet/releases/tag/v1.0.0
