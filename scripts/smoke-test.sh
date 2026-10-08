#!/usr/bin/env bash
# End-to-end check against a running instance (default: docker compose on port 8080).
# Creates the sample form, publishes it, autosaves a draft, submits it and queries it back.
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
SAMPLE="$(dirname "$0")/../samples/leave-request.form.json"
KEY="leave-request-$(date +%s)"

for _ in $(seq 1 60); do
  if curl -fsS "$BASE_URL/health/ready" > /dev/null 2>&1; then break; fi
  sleep 1
done
curl -fsS "$BASE_URL/health/ready" > /dev/null

echo "1. Create form $KEY"
jq --arg key "$KEY" '.key = $key' "$SAMPLE" \
  | curl -fsS -X POST "$BASE_URL/api/forms" -H 'Content-Type: application/json' -d @- > /dev/null

echo "2. Publish version 1"
curl -fsS -X POST "$BASE_URL/api/forms/$KEY/versions/draft/publish" > /dev/null

echo "3. Start an autosave draft with half the fields"
headers=$(mktemp)
draft=$(curl -fsS -D "$headers" -X POST "$BASE_URL/api/forms/$KEY/drafts" -H 'Content-Type: application/json' \
  -d '{"data":{"employeeName":"Jane Doe","leaveType":"sick"}}')
etag=$(grep -i '^etag:' "$headers" | cut -d' ' -f2 | tr -d '\r')
draft_id=$(jq -r '.id' <<< "$draft")
echo "   still missing: $(jq -r '[.validation.errors[].field] | join(", ")' <<< "$draft")"

echo "4. Complete the draft (If-Match: $etag)"
curl -fsS -X PUT "$BASE_URL/api/drafts/$draft_id" -H 'Content-Type: application/json' -H "If-Match: $etag" \
  -d '{"data":{"employeeName":"Jane Doe","email":"jane@example.com","leaveType":"sick","startDate":"2026-11-02","endDate":"2026-11-06","workingDays":5,"certificateNumber":"AB-123456"}}' > /dev/null

echo "5. Submit the draft"
submission=$(curl -fsS -X POST "$BASE_URL/api/drafts/$draft_id/submit")
jq -e '.data.certificateNumber == "AB-123456"' <<< "$submission" > /dev/null

echo "6. Query submissions with a JSONB filter"
count=$(curl -fsS -G "$BASE_URL/api/forms/$KEY/submissions" --data-urlencode 'filter={"leaveType":"sick"}' | jq '.items | length')
test "$count" -eq 1

echo "7. An invalid submission is rejected with 422"
status=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE_URL/api/forms/$KEY/submissions" \
  -H 'Content-Type: application/json' -d '{"data":{"employeeName":"J"}}')
test "$status" -eq 422

echo "8. A body over the 1 MiB limit is refused with 413 before it is read"
big=$(mktemp)
printf '{"data":{"employeeName":"%s"}}' "$(head -c 1200000 /dev/zero | tr '\0' 'x')" > "$big"
status=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE_URL/api/forms/$KEY/submissions" \
  -H 'Content-Type: application/json' --data-binary "@$big")
test "$status" -eq 413

rm -f "$headers" "$big"
echo "Smoke test passed."
