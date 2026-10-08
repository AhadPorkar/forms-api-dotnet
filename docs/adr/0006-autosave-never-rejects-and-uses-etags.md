# ADR 0006: Autosave never rejects input and uses ETags for concurrency

Status: accepted

## Context

Long forms are filled in over minutes or days. Clients save in the background while the user types. Two
things go wrong in naive implementations:

1. **Lost input.** If saving validates, a half-typed date or an empty required field makes the save fail.
   The user's input is then only in the browser.
2. **Lost updates.** With the same draft open in two tabs or on two devices, the last save silently overwrites
   the other.

## Decision

- `PUT /drafts/{id}` stores any JSON object up to the size limit, without validating it. The response includes
  the result of a full validation, so the UI can show progress ("4 fields left").
- Validation is enforced at `POST /drafts/{id}/submit`. That call creates the submission and deletes the draft
  in the same `SaveChanges` call, which is one transaction.
- Optimistic concurrency uses PostgreSQL's `xmin` system column, mapped as the EF Core row version. It is
  exposed as a strong ETag. `PUT` requires `If-Match` and returns `428` without it and `412` when the ETag is
  stale. `submit` accepts an optional `If-Match`.
- A draft is bound to the version that was latest when it was started. It expires after 30 days without a save
  (`Forms:DraftRetention`). Expired drafts return `404` at once and are deleted by a background service.

## Consequences

- No input is ever refused while typing, and conflicting tabs are detected rather than silently merged.
- `xmin` needs no extra column and no trigger, and it changes on every update automatically.
- Drafts may hold data that is not valid yet, by design. They are never used as business data.
- A client that receives `412` must reload the draft and decide how to merge. The API does not merge field by
  field, because only the user knows which tab is right.

## Alternatives considered

- **Validate on save, but only types.** It would still lose a half-typed date.
- **Last write wins.** Simple, but it loses data silently.
- **A version column incremented by the application.** It works, but `xmin` gives the same guarantee for free.
