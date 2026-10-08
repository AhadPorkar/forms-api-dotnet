# ADR 0005: Linear-time regular expressions against ReDoS

Status: accepted

## Context

Form designers can set a `pattern` on text fields. With a backtracking regex engine, a pattern such as
`(a+)+` and an input of a few dozen characters can take exponential time. This is Regular Expression Denial of
Service (ReDoS). The pattern comes from a form designer and the input comes from any user, so the risk is
real, and one request could keep a CPU core busy.

## Decision

- Compile every schema pattern with `RegexOptions.NonBacktracking` (available since .NET 7). This engine
  guarantees time linear in the length of the input.
- Patterns that need backtracking features (backreferences, lookarounds, atomic groups) are rejected by
  `SchemaValidator` with a clear message when the schema is saved.
- Patterns are anchored (`^(?:pattern)\z`), so they describe the whole value, as in HTML's `pattern` attribute.
  `\z` is used rather than `$`, because `$` also matches before a final line break.
- A pattern must compile on its own before it is anchored. Otherwise an unbalanced pattern such as `x)|(?:.*`
  would close the added group and match anything.
- Patterns are limited to 500 characters. Compiled expressions are cached.

## Consequences

- No timeout tuning is needed, and no request can hang on a pattern. A test runs `(a+)+` against 50,000
  characters and finishes in milliseconds.
- Backreferences and lookarounds are not available to form designers. In practice, field formats (postcodes,
  IBAN shapes, reference numbers) do not need them.
- Character classes follow .NET semantics: `\d` matches any Unicode decimal digit. Designers who want ASCII
  digits only write `[0-9]`, as the sample form does.

## Alternatives considered

- **A match timeout** (`Regex.MatchTimeout`). It limits the damage but still burns CPU up to the timeout on every
  request, and choosing the value is guesswork.
- **No patterns at all.** That would be too restrictive for real forms.
