# ADR 0003: Published versions are immutable; submissions are pinned to a version

Status: accepted

## Context

Forms change while data is being collected: a field is added, an option is renamed, a limit is lowered.
If a submission only stored the form key, nobody could later tell which rules it was validated against. A
report could no longer explain why an old record has no value for a field that is required today.

## Decision

- A form has numbered versions. Exactly one of them may be a **draft** (editable); all others are
  **published** (read-only). A partial unique index on `form_versions (form_id) WHERE status = 'Draft'`
  enforces the single draft in the database.
- Every submission and every autosave draft stores the `form_version_id` it belongs to.
- `latest` means the newest published version. Clients may submit against an older published version
  explicitly (`?version=1`), for example a mobile app that has not updated yet.
- Submissions are never migrated to a newer version automatically.

## Consequences

- Each submission can always be interpreted with its own schema (`GET /forms/{key}/versions/{n}`).
- Old clients keep working until they are updated. Whether to allow that is a product decision. If it is not
  wanted, an endpoint can reject submissions to versions older than `latest`.
- Version rows are never deleted while data references them (`ON DELETE RESTRICT`).
- Consumers that read across versions must handle more than one shape. ADR 0007 helps them see what changed.

## Alternatives considered

- **A mutable schema with migrations of existing data.** Simpler to query, but it loses history and requires
  data migrations for every edit.
- **Copying the schema into each submission.** It is self-contained, but it duplicates the schema many times
  and makes "all submissions of version 3" an expensive query.
