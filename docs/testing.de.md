# Tests

[English](testing.md) · **Deutsch**

| Ebene | Projekt | Anzahl | Was nachgewiesen wird | Laufzeit |
| --- | --- | --- | --- | --- |
| Unit | `tests/Forms.Core.Tests` | 109 | Validierungslogik, Schemaprüfung und Kompatibilitätsregeln, unabhängig von HTTP und Datenbank. | < 1 s |
| Integration | `tests/Forms.Api.IntegrationTests` | 52 | Die echte Anwendung über HTTP gegen PostgreSQL 16 im Container: Migrationen, `jsonb`-Abbildung, die GIN-Containment-Abfrage, der partielle Unique-Index, Nebenläufigkeit über `xmin`, Keyset-Paging, Problem Details und OpenAPI. | etwa 5 s, sobald das Image geladen ist |
| End-to-End | `scripts/smoke-test.sh` | 8 Schritte | Das Container-Image mit Docker Compose: anlegen, veröffentlichen, mit ETag zwischenspeichern, einreichen, filtern, ablehnen, zu großen Body abweisen. | etwa 30 s einschließlich Build |

```bash
dotnet test                                    # beide Projekte; Docker muss laufen
dotnet test tests/Forms.Core.Tests             # nur Unit-Tests, ohne Docker
dotnet test --collect:"XPlat Code Coverage"    # Coverage im Cobertura-Format, wie in der CI
docker compose up -d --build && ./scripts/smoke-test.sh
```

## Unit-Tests

- **Ein Beispiel, viele Tests.** Die meisten Validierungstests gehen von
  [`samples/leave-request.form.json`](../samples/leave-request.form.json) aus und ändern genau eine Sache.
  Damit ist auch das Beispiel aus der README getestet.
- **Jeder Fehlercode** hat mindestens einen Test, zusammen mit dem Feld, an dem er gemeldet wird.
- **Bedingte Logik.** Die Tests decken ausgeblendete Felder ab, die keine Pflicht sind und aus der Ausgabe
  entfernt werden, außerdem fortgesetztes Ausblenden (ein Feld, das von einem ausgeblendeten Feld abhängt, ist
  ebenfalls ausgeblendet), Verschachtelung mit `all`/`any` und „equals“ bei Mehrfachauswahl im Sinne von „enthält“.
- **ReDoS.** `(a+)+` gegen 50.000 Zeichen plus `!`: Mit Backtracking wäre das praktisch endlos. Der Test
  verlangt weniger als 2 Sekunden; tatsächlich sind es Millisekunden.
- **Schemaprüfung.** Die Tests prüfen den genauen `path` jedes Fehlers, weil ein Formular-Designer damit das
  fehlerhafte Eingabefeld markiert.
- **Kompatibilität.** Jede Änderungsart wird in beide Richtungen getestet (verschärft und gelockert, hinzugefügt
  und entfernt).

## Integrationstests

`ApiFactory` startet pro Testlauf einen Container `postgres:16-alpine` (Assembly Fixture von xUnit v3) und hostet
das echte `Program` mit `WebApplicationFactory`. Die Migrationen laufen beim Start, genau wie in Docker Compose.
Die Tests trennen sich über eindeutige Formularschlüssel, statt die Datenbank zurückzusetzen. Das hält den Lauf
schnell und zeigt zugleich, dass die Abfragen korrekt nach Formular filtern.

Zeitabhängiges Verhalten (Ablauf von Entwürfen) nutzt eine `TestClock`, die den registrierten `TimeProvider`
ersetzt. Kein Test wartet mit `sleep`.

Wichtige Szenarien:

| Test | Warum er wichtig ist |
| --- | --- |
| `Breaking_draft_needs_explicit_confirmation_to_publish` | Der vollständige Versionierungsablauf einschließlich der `409`-Sperre. |
| `Submissions_stay_pinned_to_the_version_they_were_made_against` | Version 1 nimmt die alte Form weiter an, nachdem Version 2 ein Pflichtfeld ergänzt hat. |
| `Submissions_can_be_filtered_by_jsonb_containment` | `@>` auf einfachen Werten und innerhalb von Arrays. |
| `Submissions_page_newest_first_with_a_cursor` | Keyset-Paging liefert jeden Eintrag genau einmal und in der richtigen Reihenfolge. |
| `Update_requires_if_match_and_rejects_a_stale_etag` | `428` ohne `If-Match`, `412` für einen veralteten Tab, und die neueren Daten bleiben erhalten. |
| `Submitting_a_complete_draft_creates_a_submission_and_removes_the_draft` | Eine ungültige Einreichung behält den Entwurf; eine gültige ist atomar. |
| `Database_allows_only_one_draft_per_form` | Der partielle Unique-Index greift auch dann, wenn der Anwendungscode umgangen wird. |
| `Stale_draft_version_cannot_overwrite_a_published_one` | Das `xmin`-Token auf Versionen macht aus einem verlorenen Update eine Nebenläufigkeitsausnahme. |
| `InputHardeningTests` (10 Tests) | JSON, das PostgreSQL oder .NET nicht halten kann, Steuerzeichen in Schlüsseln, Null-Elemente in Schemas und fehlerhaftes `If-Match` liefern immer 4xx, nie 500. |

## Continuous Integration

`.github/workflows/ci.yml` hat vier Jobs:

1. **build-test**: `dotnet format --verify-no-changes`, ein Release-Build mit Warnungen als Fehler und beide
   Testprojekte mit Coverage. TRX-Ergebnisse und Coverage werden als Artefakte hochgeladen.
2. **container**: `docker compose config`, Bau des Images, `docker compose up`, Smoke-Test und bei Fehlern die
   Logs der API.
3. **migrations**: prüft, dass das EF-Core-Modell keine Änderungen ohne Migration enthält.
4. **docs**: `markdownlint` und eine Offline-Prüfung aller Links in den Markdown-Dateien.
