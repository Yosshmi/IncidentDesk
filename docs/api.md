# API usage

Base URL: `http://localhost:8080/api/v1`. JSON property names use camel case. IDs and versions are UUIDs; dates are ISO 8601 timestamps. Enum bodies use names such as `High` and `Investigating`, not integer values. Interactive documentation is at `/docs` in Development and Testing environments.

## Authentication and permissions

`POST /auth/login` accepts `{ "email": "...", "password": "..." }` and returns `tokenType`, `accessToken`, `expiresIn` and `refreshToken`. Send `Authorization: Bearer <accessToken>` thereafter. These are ASP.NET Core Identity opaque tokens, not JWTs; clients must not parse them. `POST /auth/refresh` accepts `{ "refreshToken": "..." }`. `GET /auth/me` returns the authenticated user's ID, email, display name and roles. Login and refresh share a limit of 30 requests per IP per minute; five failed sign-in attempts lock an account for 15 minutes.

There is no public registration endpoint. The demo seeder creates four local accounts. Reporters can create incidents, search/read their own incidents and add comments to them. Engineers can access the queue and perform management actions. An incident outside a reporter's visibility returns `404`. `GET /users?role=Engineer` is an engineer-only assignment directory returning an array of `{ id, displayName }`.

## Endpoints

| Method and path | Body / result | Permission |
| --- | --- | --- |
| `POST /incidents` | `{ title, description, severity? }` → `201`, incident, Location and ETag | Authenticated |
| `GET /incidents` | Filtered, paginated incident list | Own incidents / engineer queue |
| `GET /incidents/{id}` | Incident and ETag | Visible incident |
| `PUT /incidents/{id}` | `{ title, description, severity }` | Engineer + If-Match |
| `PUT /incidents/{id}/assignment` | Explicit `{ assigneeId }`; null unassigns where allowed | Engineer + If-Match |
| `POST /incidents/{id}/transitions` | `{ status, resolutionNote? }` | Engineer + If-Match |
| `GET /incidents/{id}/comments` | Paginated comments | Visible incident |
| `POST /incidents/{id}/comments` | `{ body }` → `201` | Visible incident + If-Match |
| `GET /incidents/{id}/history` | Paginated status history | Visible incident |
| `POST /incidents/{id}/summary-draft` | No body → `{ text, sourceVersion }` and source ETag | Engineer |
| `PUT /incidents/{id}/summary` | `{ text }` → updated incident | Engineer + If-Match |

There is no delete endpoint. Comments and history are append-only. All notes are shared with the reporter.

## Search and pagination

`GET /incidents` accepts:

| Parameter | Meaning |
| --- | --- |
| `q` | Case-insensitive literal substring of title or description; up to 200 characters |
| `status` | `Open`, `Investigating` or `Resolved` |
| `severity` | `Low`, `Medium`, `High` or `Critical` |
| `reporterId`, `assigneeId` | Exact user UUID filter |
| `unassigned` | `true` for unassigned incidents; `false` for assigned incidents |
| `createdFrom`, `createdTo` | Inclusive timestamp bounds; include a time-zone offset, preferably `Z` |
| `page`, `pageSize` | One-based page, default 1; size 1–100, default 20 |

Combining `assigneeId` with `unassigned=true`, reversing date bounds, unknown enum values and invalid pagination produces `400`. Filters never broaden a reporter's access. Results are newest first with an ID tie-breaker; offset pagination can shift when records are inserted between requests.

All paginated endpoints return:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 20,
  "totalCount": 0
}
```

Comments and history are ordered oldest first with an ID tie-breaker. The incident representation includes `id`, `title`, `description`, `severity`, `status`, `reporterId`, `assigneeId`, `createdAt`, `updatedAt`, `resolvedAt`, `resolutionNote`, `summary`, `summaryReviewedById`, `summaryReviewedAt` and `version`.

## Validation and transitions

Titles are required and limited to 200 characters; descriptions to 10,000; comments and resolution notes to 5,000; reviewed summaries to 10,000. Whitespace-only required text and NUL characters are rejected. Unknown JSON properties and numeric enum values return `400`. Severity defaults to `Medium` when creating an incident, but is required on general updates. Assignment requests must explicitly include `assigneeId`, even when its value is null.

Allowed transitions are strictly `Open → Investigating → Resolved`. Investigation requires an engineer assignment. Resolution requires a nonblank resolution note. The recorded actor is always the authenticated user. The server controls IDs, timestamps, initial state, history and reviewer attribution.

## Concurrency

GET and successful incident writes expose a strong quoted ETag, for example `"93d46e99-8338-4b07-a05a-0e5c125b5e41"`. Every write to an existing incident, including a new comment, requires exactly one strong `If-Match` value. Wildcards, weak tags and lists of tags are rejected. Draft generation is read-only and does not require this header.

If two engineers read version A, the first successful save advances the version; a save using A then receives `412 Precondition Failed`. The client must fetch the latest incident, compare changes and retry with its new ETag only after review. Blindly refetching and resubmitting would defeat the protection. Concurrent history/comment writes either commit with the incident update or roll back with it.

## Example workflow

These examples require `curl` and `jq`. They use only local demo data. Tokens are held in the current shell; do not add them to source control.

```sh
BASE=http://localhost:8080/api/v1
REPORTER_TOKEN=$(curl --fail-with-body --silent "$BASE/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"reporter1@example.test","password":"IncidentDesk1!"}' | jq -r .accessToken)
ENGINEER_TOKEN=$(curl --fail-with-body --silent "$BASE/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"engineer1@example.test","password":"IncidentDesk1!"}' | jq -r .accessToken)
ENGINEER_ID=$(curl --fail-with-body --silent "$BASE/auth/me" \
  -H "Authorization: Bearer $ENGINEER_TOKEN" | jq -r .id)

INCIDENT_ID=$(curl --fail-with-body --silent "$BASE/incidents" \
  -H "Authorization: Bearer $REPORTER_TOKEN" -H 'Content-Type: application/json' \
  -d '{"title":"Intermittent checkout timeout","description":"Timeouts increased after an upstream rollout.","severity":"High"}' | jq -r .id)

# Read the ETag without writing headers or tokens to disk.
etag() {
  curl --fail-with-body --silent --dump-header - --output /dev/null \
    -H "Authorization: Bearer $ENGINEER_TOKEN" "$BASE/incidents/$INCIDENT_ID" \
    | awk 'tolower($1) == "etag:" {gsub("\r", "", $2); print $2}'
}

VERSION=$(etag)
curl --fail-with-body --silent -X PUT "$BASE/incidents/$INCIDENT_ID/assignment" \
  -H "Authorization: Bearer $ENGINEER_TOKEN" -H "If-Match: $VERSION" \
  -H 'Content-Type: application/json' -d "{\"assigneeId\":\"$ENGINEER_ID\"}"

VERSION=$(etag)
curl --fail-with-body --silent -X POST "$BASE/incidents/$INCIDENT_ID/transitions" \
  -H "Authorization: Bearer $ENGINEER_TOKEN" -H "If-Match: $VERSION" \
  -H 'Content-Type: application/json' -d '{"status":"Investigating"}'

VERSION=$(etag)
curl --fail-with-body --silent -X POST "$BASE/incidents/$INCIDENT_ID/comments" \
  -H "Authorization: Bearer $ENGINEER_TOKEN" -H "If-Match: $VERSION" \
  -H 'Content-Type: application/json' -d '{"body":"The upstream release changed timeout behavior; rollback is being verified."}'

VERSION=$(etag)
curl --fail-with-body --silent -X POST "$BASE/incidents/$INCIDENT_ID/transitions" \
  -H "Authorization: Bearer $ENGINEER_TOKEN" -H "If-Match: $VERSION" \
  -H 'Content-Type: application/json' \
  -d '{"status":"Resolved","resolutionNote":"Rolled back the upstream release and verified timeout rates returned to baseline."}'

curl --fail-with-body --silent "$BASE/incidents?status=Resolved&severity=High&page=1&pageSize=10" \
  -H "Authorization: Bearer $ENGINEER_TOKEN" | jq
```

Refreshing ETags between steps is appropriate here because each step is a new deliberate action. For a real edit form, retain the version from when the engineer loaded that form.

## Draft summary review

With AI enabled, request a draft:

```sh
curl --fail-with-body --silent -X POST "$BASE/incidents/$INCIDENT_ID/summary-draft" \
  -H "Authorization: Bearer $ENGINEER_TOKEN"
```

The response's UUID `sourceVersion` and ETag identify the same database snapshot of the incident and its notes. The draft has not been saved. Review its factual claims and edit the text, then save using the returned ETag in `If-Match`:

```sh
curl --fail-with-body --silent -X PUT "$BASE/incidents/$INCIDENT_ID/summary" \
  -H "Authorization: Bearer $ENGINEER_TOKEN" -H 'If-Match: "SOURCE_VERSION_FROM_DRAFT"' \
  -H 'Content-Type: application/json' \
  -d '{"text":"Engineer-reviewed summary of the investigation and verified resolution."}'
```

Replace `SOURCE_VERSION_FROM_DRAFT` with the actual UUID, preserving the quotes. A `412` means the source changed; compare the new notes and regenerate or revise the draft before saving. Engineers may also write a summary manually with no LLM configured.

Draft generation is limited to five requests per engineer per minute. With the provider configured, no investigation notes produces `400` with code `insufficient_notes`. More than 100 notes or more than 40,000 combined characters in the title, description, status, resolution note and note bodies produces `400` with code `summary_input_too_large`; input is not silently truncated. The default provider timeout is 30 seconds, and there are no automatic retries.

Provider failures have stable codes: disabled/missing/invalid configuration returns `503 summary_unavailable`; upstream errors, malformed output, refusal or incomplete output return `502 summary_provider_failed`; timeout returns `504 summary_timeout`. None saves a summary or changes the incident.

## Errors

Errors use `application/problem+json`, including an HTTP status, readable title/detail, a request trace ID and an application error code where available. Field validation includes an `errors` dictionary. Do not rely on message wording as a stable machine-readable contract.

| Status | Meaning |
| --- | --- |
| `400` | Invalid JSON/input, malformed precondition or a missing required resolution note |
| `401` | Missing/expired token or invalid credentials |
| `403` | Authenticated user lacks the required role |
| `404` | Incident absent or outside the user's visibility |
| `409` | Invalid workflow transition or operation on frozen incident fields |
| `412` | Incident version changed |
| `428` | `If-Match` was omitted |
| `429` | Request rate limit exceeded |
| `502` / `503` / `504` | Summary provider failed / unavailable or disabled / timed out |

Liveness is available at `/health/live`. `/health/ready` returns `503` when PostgreSQL is unreachable or committed migrations remain unapplied; otherwise it returns `200`.
