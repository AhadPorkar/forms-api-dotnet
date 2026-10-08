# ADR 0002: Store schemas and submissions as `jsonb`, not as entity-attribute-value rows

Status: accepted

## Context

The fields of a form are defined at run time. The classic relational answer is an entity-attribute-value
(EAV) model: one row per submission and field (`submission_id, field_key, value_text, value_number, ...`).
EAV is flexible, but reading one submission needs a pivot. Every filter needs a self-join per condition, types
are lost or spread over several columns, and the tables grow by the number of fields times the number of
submissions.

## Decision

Store each schema version and each submission as one `jsonb` document:

- `form_versions.schema` holds the whole `FormSchema`.
- `submissions.data` and `submission_drafts.data` hold the normalised data.
- `submissions.data` has a GIN index with `jsonb_path_ops`. Queries use containment
  (`data @> '{"leaveType":"sick"}'`), which this index supports.

## Consequences

- Reading or writing a submission is one row. The stored document has exactly the content the API returned
  when the submission was created.
- Containment filters are indexed and also work inside arrays (`{"notify":["hr"]}`).
- Validation happens in the application (ADR 0004), not in database constraints. The database still enforces
  the relationships: versions, uniqueness, and pinning.
- Range queries on a field (`workingDays > 3`) are not covered by the GIN index. If such reports become
  important, an expression index (`((data->>'workingDays')::int)`) can be added per form. Reporting workloads
  could also be fed to a read model.
- `jsonb` normalises key order and whitespace. A submission is created in schema order but reads back in the
  order `jsonb` chose. Clients must not depend on key order, which JSON does not guarantee anyway.

## Alternatives considered

- **EAV.** Rejected for the reasons above.
- **One table per form, generated DDL.** Gives the best query performance, but schema changes become
  migrations at run time, and versioning becomes very hard.
- **A document database.** It would solve storage, but the relational guarantees for forms and versions
  (unique keys, one draft per form, foreign keys) would have to move into application code.
