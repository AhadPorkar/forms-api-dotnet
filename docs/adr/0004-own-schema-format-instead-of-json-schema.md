# ADR 0004: An own schema format and validation engine instead of JSON Schema

Status: accepted

## Context

JSON Schema is the standard for describing JSON documents, and good .NET validators exist for it. A form
definition, however, needs more than a JSON Schema expresses well:

- **Labels, help texts and option labels** for rendering.
- **Visibility.** A hidden field must not be validated, and its value must be removed. JSON Schema can express
  `if/then/else`, but not "ignore and drop this value".
- **Cascading conditions.** A field that depends on a hidden field is hidden too.
- **Error messages** in the words of the form ("First day is required."), with stable codes per field.
- **Compatibility analysis** between versions (ADR 0007). On arbitrary JSON Schema that is an open research
  problem; on a closed, form-specific model it is a straightforward comparison.

## Decision

Define a small, closed schema model in `Forms.Core` (`FormSchema`, `FieldDefinition`, `FieldConstraints`,
`Condition` and `CrossFieldRule`) with its own validator. Check every schema with `SchemaValidator` before it is
stored, so that run-time validation never meets an inconsistent schema.

## Consequences

- The semantics are explicit and fully tested (109 unit tests), including edge cases such as cascading
  visibility and multi-select conditions.
- Clients cannot reuse generic JSON Schema tooling directly. A one-way export to JSON Schema (without the
  visibility semantics) can be added if a client needs it.
- New field types are a code change, not a configuration change. That is deliberate: every type needs reading,
  validation, condition semantics and a compatibility rule.

## Alternatives considered

- **JSON Schema plus UI Schema** (as in JSON Forms or react-jsonschema-form). Widely known, but visibility,
  removal of hidden data and compatibility analysis would still need custom code on top.
- **FluentValidation with dynamic rules.** Built for compile-time models; rules defined at run time would fight
  the library.
