# ADR 0007: Publishing a breaking change needs explicit confirmation

Status: accepted

## Context

Published versions are immutable (ADR 0003), so publishing a new version never corrupts stored data. It can
still surprise people. A report may expect a field that the new version removed. An integration may send data
that the new version rejects. A form designer usually does not see these effects when editing a schema.

## Decision

`SchemaCompatibility.Compare(from, to)` classifies each difference. The definition of **breaking** is: data that
was valid for the old version may be invalid for the new one, or a consumer may find a field gone or changed in
shape.

| Change | Breaking |
| --- | --- |
| Field removed; type changed (except Integer → Number, Text → LongText) | yes |
| Required field added; field became required; `requiredWhen` added or changed | yes |
| Lower bound raised or upper bound lowered; pattern added or changed | yes |
| Option removed; cross-field rule added or changed | yes |
| `visibleWhen` added, removed or changed | yes |
| Optional field added; field became optional; bound loosened; pattern removed | no |
| Option added; label or help text changed; rule removed | no |

`PUT /versions/draft` returns this report against the latest published version. `POST /versions/draft/publish`
returns `409` with the report if any change is breaking, unless the caller sends `acceptBreakingChanges=true`.

## Consequences

- The person publishing sees exactly what will break and confirms it on purpose. An admin UI can show the
  report as a confirmation dialog.
- The rules are conservative where precision is impossible. Whether one regular expression accepts a superset
  of another is not decidable in general, so every pattern change counts as breaking. The same holds for
  visibility: showing a field in more cases subjects more data to its checks, hiding it in more cases drops data
  that used to be stored, and telling the two apart would mean comparing arbitrary conditions.
- `GET /versions/compare?from=&to=` gives the same report for any two versions, for example for release notes
  or data pipelines.

## Alternatives considered

- **Forbid breaking changes.** Too strict: real forms do need to drop fields.
- **Warn only.** Warnings in a response body are easily ignored by scripts and CI jobs that publish forms.
