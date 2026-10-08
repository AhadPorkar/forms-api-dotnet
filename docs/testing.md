# Testing

**English** · [Deutsch](testing.de.md)

| Level | Project | Count | What it proves | Runtime |
| --- | --- | --- | --- | --- |
| Unit | `tests/Forms.Core.Tests` | 109 | Validation semantics, schema checks and compatibility rules, independent of HTTP and the database. | < 1 s |
| Integration | `tests/Forms.Api.IntegrationTests` | 52 | The real application over HTTP against PostgreSQL 16 in a container: migrations, `jsonb` mapping, the GIN containment query, the partial unique index, `xmin` concurrency, keyset paging, problem details and OpenAPI. | about 5 s after the image is pulled |
| End to end | `scripts/smoke-test.sh` | 8 steps | The published container image with Docker Compose: create, publish, autosave with ETag, submit, filter, reject, refuse an oversized body. | about 30 s including the build |

```bash
dotnet test                                    # both projects; Docker must be running
dotnet test tests/Forms.Core.Tests             # unit tests only, no Docker needed
dotnet test --collect:"XPlat Code Coverage"    # Cobertura coverage, as in CI
docker compose up -d --build && ./scripts/smoke-test.sh
```

## Unit tests

- **One sample, many tests.** Most validator tests start from [`samples/leave-request.form.json`](../samples/leave-request.form.json)
  and change one thing. The sample in the README is therefore tested too.
- **Every error code** has at least one test, together with the field it is reported on.
- **Conditional logic.** The tests cover hidden fields that are not required and are dropped from the output,
  cascading visibility (a field that depends on a hidden field is hidden too), `all`/`any` nesting, and
  "equals" on a multi-select meaning "contains".
- **ReDoS.** `(a+)+` against 50,000 characters plus `!`: on a backtracking engine this would not finish in our
  lifetime. The test asserts that it finishes in under 2 seconds. It takes milliseconds.
- **Schema checks.** The tests check the exact `path` of each error, because a form designer UI uses that path to
  highlight the faulty input.
- **Compatibility.** Each change kind is tested in both directions (tightened and loosened, added and removed).

## Integration tests

`ApiFactory` starts one `postgres:16-alpine` container per test run (an xUnit v3 assembly fixture) and hosts the
real `Program` with `WebApplicationFactory`. Migrations run at start-up, exactly as in Docker Compose. Tests
isolate themselves with unique form keys instead of resetting the database. This keeps the run fast and also
shows that the queries filter correctly by form.

Time-dependent behaviour (draft expiry) uses a `TestClock` that replaces the registered `TimeProvider`,
so no test sleeps.

Notable scenarios:

| Test | Why it matters |
| --- | --- |
| `Breaking_draft_needs_explicit_confirmation_to_publish` | The full versioning workflow including the `409` gate. |
| `Submissions_stay_pinned_to_the_version_they_were_made_against` | Version 1 still accepts the old shape after version 2 adds a required field. |
| `Submissions_can_be_filtered_by_jsonb_containment` | `@>` on scalar values and inside arrays. |
| `Submissions_page_newest_first_with_a_cursor` | Keyset paging returns every item exactly once and in order. |
| `Update_requires_if_match_and_rejects_a_stale_etag` | `428` without `If-Match`, `412` for a stale tab, and the newer data survives. |
| `Submitting_a_complete_draft_creates_a_submission_and_removes_the_draft` | Invalid submit keeps the draft; valid submit is atomic. |
| `Database_allows_only_one_draft_per_form` | The partial unique index works even when application code is bypassed. |
| `Stale_draft_version_cannot_overwrite_a_published_one` | The `xmin` token on versions turns a lost update into a concurrency exception. |
| `InputHardeningTests` (10 tests) | JSON that PostgreSQL or .NET cannot hold, control characters in keys, null elements in schemas and malformed `If-Match` all get a 4xx, never a 500. |

## Continuous integration

`.github/workflows/ci.yml` runs four jobs:

1. **build-test**: `dotnet format --verify-no-changes`, a Release build with warnings as errors, and both test
   projects with coverage. TRX results and coverage are uploaded as artifacts.
2. **container**: `docker compose config`, the image build, `docker compose up`, the smoke test, and the API logs
   if anything fails.
3. **migrations**: checks that the EF Core model has no pending changes without a migration.
4. **docs**: `markdownlint` and an offline link check of all Markdown files.
