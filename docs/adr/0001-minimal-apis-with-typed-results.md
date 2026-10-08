# ADR 0001: Minimal APIs with typed results

Status: accepted

## Context

The service has about fifteen endpoints. Each endpoint is a thin adapter between HTTP and the core library.
ASP.NET Core offers two programming models: MVC controllers, and Minimal APIs with route groups.

## Decision

Use Minimal APIs. Every handler is a `static` method that returns `Results<...>` (for example
`Results<Created<SubmissionResponse>, ValidationProblem, ProblemHttpResult>`). Endpoints are grouped by
feature in `MapFormEndpoints`, `MapSubmissionEndpoints` and `MapDraftEndpoints`.

## Consequences

- The possible responses of each handler are part of its signature. The compiler checks them, and the OpenAPI
  generator documents them without `[ProducesResponseType]` attributes.
- Handlers have no hidden state; dependencies arrive as parameters. They are easy to read and to test.
- Cross-cutting behaviour (authorisation, rate limiting) is added per route group with one call.
- Minimal APIs are the model Microsoft develops further, including native AOT support.

## Alternatives considered

- **MVC controllers.** Familiar, but they add a layer (model binding conventions, filters, attributes) that
  this service does not need.
- **A library such as FastEndpoints or Carter.** Nice ergonomics, but an extra dependency for little gain at
  this size.
