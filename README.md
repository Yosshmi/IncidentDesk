# IncidentDesk

IncidentDesk is a C# / ASP.NET Core 10 API for reporting production incidents, assigning engineers, recording investigation notes and tracking resolution. PostgreSQL stores incidents, users, comments and an append-only status history. Optimistic concurrency prevents one engineer from silently overwriting another engineer's changes.

The project focuses on the backend. An optional LLM can draft an incident summary, but an engineer must review and explicitly save it. The normal workflow and automated tests do not require an AI account or API key.

## What is included

- REST endpoints with validated input, search, filters and bounded pagination.
- An enforced `Open → Investigating → Resolved` workflow, engineer assignment and required resolution notes.
- Reporter/engineer authorization with ASP.NET Core Identity and opaque bearer tokens.
- EF Core migrations, PostgreSQL 18, foreign keys, indexed queries and atomic incident/history writes.
- Strong ETags and mandatory `If-Match` preconditions on existing-incident writes.
- xUnit unit tests and HTTP integration tests against a real, disposable PostgreSQL database.
- A non-root Docker image, Compose startup, health endpoints and GitHub Actions checks.

## Run the local demo

Install Docker with Compose and start its engine. Python 3 is needed for the smoke-test and handoff scripts. The Docker path does **not** require a locally installed .NET SDK.

```sh
cp .env.example .env
python3 - <<'PY'
from pathlib import Path
import secrets
path = Path('.env')
path.write_text(path.read_text().replace('replace-with-a-random-hex-password', secrets.token_hex(24)))
PY
docker compose up --build --detach --wait --wait-timeout 180
python3 scripts/smoke.py
```

Open [API documentation](http://localhost:8080/docs). The API listens on `http://localhost:8080`; PostgreSQL is available only on `127.0.0.1:5432`. Change `API_PORT` or `POSTGRES_PORT` in `.env` if those ports are occupied, and pass the new URL to `scripts/smoke.py --base-url ...`.

Compose waits for PostgreSQL, runs committed migrations and the development seeder in a one-shot container, then starts the API. PostgreSQL data and authentication encryption keys survive container replacement in separate named volumes. A normal API startup does not apply migrations.

The development accounts all use **`IncidentDesk1!`**:

| Email | Role |
| --- | --- |
| `reporter1@example.test` | Reporter |
| `reporter2@example.test` | Reporter |
| `engineer1@example.test` | Engineer |
| `engineer2@example.test` | Engineer |

These accounts are created only by the explicit development seed command. The supplied Compose stack is a local demonstration configuration; do not expose it publicly with these accounts or development settings.

```sh
docker compose logs --follow api
docker compose down
```

`docker compose down` preserves data. **`docker compose down --volumes` deletes the local database and token-protection keys.** Recreating the keys invalidates existing access and refresh tokens.

## Develop and test

Install the .NET 10 SDK version recorded in `global.json` (currently `10.0.401`) and keep Docker running for integration tests.

```sh
dotnet tool restore
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --configuration Release --no-restore
dotnet test --solution IncidentDesk.sln --no-restore --configuration Release --report-trx --results-directory artifacts/test-results
```

Unit tests require no database. Integration tests use Testcontainers to start their own PostgreSQL 18 instance, apply the real migrations and exercise the HTTP API. They use ephemeral authentication-protection keys, do not use the Compose development database and do not contact OpenAI. Docker must be available even if the application itself is running outside Docker.

To run the API directly with the SDK, first follow the `.env` setup above and start just PostgreSQL:

```sh
docker compose up --detach postgres
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__IncidentDesk='Host=localhost;Port=5432;Database=incidentdesk;Username=incidentdesk;Password=YOUR_LOCAL_DATABASE_PASSWORD'
export DataProtection__KeyPath="$PWD/.keys"
dotnet run --project src/IncidentDesk.Api -- --migrate --seed-demo
dotnet run --project src/IncidentDesk.Api -- --urls http://localhost:8080
```

Replace `YOUR_LOCAL_DATABASE_PASSWORD` with the value in your local `.env`; adjust the port if necessary. Stop the Compose API before running a second server on the same port. Configuration comes from normal ASP.NET Core configuration providers; `.env` is read by Compose, not automatically by `dotnet run`.

## Workflow and API

Reporters can create incidents, view their own incidents and add comments. Engineers can view the entire queue, assign engineers, update incident details, change status and save reviewed summaries. All investigation comments are visible to the incident's reporter; there is no private-note feature.

An incident starts `Open`. Moving to `Investigating` requires an engineer assignment. Only `Investigating` can become `Resolved`, and the request must include a nonblank resolution note. Reopening, skipping stages and same-state transitions are rejected. Resolved incident details and assignment are frozen; comments and reviewed summaries remain writable.

Each incident read returns an `ETag`. Send that exact quoted value in `If-Match` when updating the incident, assignment, status, comments or summary. Missing versions return `428`; stale versions return `412`. Refetch and review changes before retrying.

See [API usage and copyable curl examples](docs/api.md), [architecture and implementation decisions](docs/architecture.md), and [operations, AI setup and handoff instructions](docs/operations.md).

## Optional AI draft

After the core flow works, set `OPENAI_ENABLED=true` and a local `OPENAI_API_KEY` in `.env`, then recreate the API with `docker compose up --detach --force-recreate api`. The default model is `gpt-4.1-mini`, configurable through `OPENAI_MODEL`.

Only an engineer can request `POST /api/v1/incidents/{id}/summary-draft`. It returns a draft, `sourceVersion` and the source snapshot's ETag without writing to the database. The engineer reviews/edits the draft and saves it separately through `PUT /api/v1/incidents/{id}/summary` with that ETag in `If-Match`. Drafting requires investigation notes and supports at most 100 notes and 40,000 source characters. Provider failures leave the incident untouched. Treat generated content as a draft, and enable the integration only for incident data your organization permits sending to that provider.

## Repository and handoff

```text
src/IncidentDesk.Domain/             Workflow and domain rules
src/IncidentDesk.Api/                HTTP, authentication, persistence and summaries
tests/IncidentDesk.UnitTests/        Domain tests
tests/IncidentDesk.IntegrationTests/ Real PostgreSQL and HTTP tests
docs/                               API, architecture and operating notes
scripts/                            Smoke test and portable Git handoff
```

GitHub Actions restores locked packages, checks formatting, builds, runs tests, uploads TRX test results, builds the Docker image and smoke-tests the Compose stack. The workflow runs after the repository is pushed to GitHub; local verification does not imply a remote CI run has happened.

After all work is committed and `git status` is clean:

```sh
python3 scripts/package-handoff.py --output ../IncidentDesk-handoff.zip
```

The zip includes committed source and a sanitized `.git` directory, preserves real commit history, and configures local author/committer identity as `Yosshmi <yosshmi04@gmail.com>`. Ignored files such as `.env`, build output, tokens and database volumes are excluded. The script verifies an extracted copy before reporting success. See [the recipient checklist](docs/operations.md#send-the-repository-to-someone-else) before sharing.

## Deliberate limits

This is a single-service portfolio implementation, not a deployed incident-response platform. It has no React dashboard, Azure resources, public registration, account-administration UI, notifications, attachments, incident reopening, private notes, SLA engine or multi-tenant model. Local Identity tokens are suitable for the demo; enterprise deployment should use a managed identity provider, TLS, appropriate key protection, database backups and an operational review. No real employer incident data is included.
