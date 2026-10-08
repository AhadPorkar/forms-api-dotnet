# ADR 0008: Integration tests against real PostgreSQL with Testcontainers

Status: accepted

## Context

Much of the behaviour that matters lives in PostgreSQL: `jsonb` mapping and containment (`@>`), the GIN index
operator class, the partial unique index, `xmin` concurrency, `ExecuteDelete`, and the translation of
`Guid` comparisons for keyset paging. EF Core's in-memory provider and SQLite support none of these, or behave
differently.

## Decision

- Integration tests start a real `postgres:16-alpine` container with Testcontainers for .NET. One container
  is shared by the test assembly (an xUnit v3 `AssemblyFixture`).
- The application is hosted unchanged with `WebApplicationFactory<Program>`. Tests talk HTTP only, except one
  test that bypasses the API on purpose to prove a database constraint.
- Migrations are applied at start-up, the same path as in Docker Compose. A separate CI job checks that the
  model has no changes without a migration.
- Tests isolate themselves with unique form keys instead of truncating tables between tests.

## Consequences

- The tests prove the real SQL, the real schema and the real HTTP contract. A green run means the system works.
- Docker is required to run them locally. Unit tests in `Forms.Core.Tests` run without Docker.
- The first run pulls the image. After that the suite takes about five seconds.

## Alternatives considered

- **EF Core in-memory provider.** It is fast, but it would test a database that does not exist.
- **A shared test database.** It leads to flaky tests and "works on my machine" problems.
