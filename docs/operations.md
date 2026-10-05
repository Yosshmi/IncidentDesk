# Operations and handoff

## Configuration

ASP.NET Core accepts nested configuration keys as environment variables using double underscores. Compose maps the shorter names in `.env` to those variables.

| ASP.NET Core setting | Compose `.env` input | Purpose |
| --- | --- | --- |
| `ConnectionStrings__IncidentDesk` | `POSTGRES_PASSWORD` plus local fixed database/user | PostgreSQL connection string |
| `ASPNETCORE_ENVIRONMENT` | Set to Development by supplied Compose | Local demo environment |
| `DataProtection__KeyPath` | Set to `/app/keys` by supplied Compose | Persist authentication-protection keys |
| `OpenAI__Enabled` | `OPENAI_ENABLED`, default `false` | Enable draft generation |
| `OpenAI__ApiKey` | `OPENAI_API_KEY`, default empty | Provider credential |
| `OpenAI__Model` | `OPENAI_MODEL`, default `gpt-4.1-mini` | Draft-generation model |
| `OpenAI__TimeoutSeconds` | No short Compose mapping; default `30` | Provider timeout, integer from 1 to 120 seconds |

`API_PORT` and `POSTGRES_PORT` change the local published ports, not the container's internal addresses. `.env.example` contains placeholders only. Runtime `.env` and `.keys` directories must remain untracked. Use a random hexadecimal PostgreSQL password with the provided connection-string interpolation, as demonstrated in the README.

The Compose configuration binds published ports to loopback. The runtime image runs as the built-in non-root `app` user. Its `/app/keys` directory is created with that ownership at build time; Docker initializes the new named volume with those permissions.

## Migrations and demo data

The repository includes migrations and the EF model snapshot. To apply migrations without starting the server:

```sh
dotnet run --project src/IncidentDesk.Api -- --migrate
```

For the explicit development bootstrap:

```sh
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/IncidentDesk.Api -- --migrate --seed-demo
```

Configure the database connection before either command. The seeder is idempotent, so re-running the local bootstrap does not create duplicate demo accounts. The API does not silently seed on normal startup. Do not enable the demo seeder in a deployed environment.

When changing the EF model, create and inspect a new migration, then test it against an empty database and the previous schema:

```sh
dotnet tool restore
dotnet ef migrations add DescribeTheChange --project src/IncidentDesk.Api
dotnet test --solution IncidentDesk.sln --configuration Release
```

Never replace committed migration history with `EnsureCreated`. For deployment, inspect migrations, take an appropriate backup and run the migration step before starting new instances. Use separate migration/runtime database privileges when deploying beyond the demo. Updating a PostgreSQL major version is a database upgrade operation, not merely an image-tag replacement.

PostgreSQL 18's official image stores versioned data beneath `/var/lib/postgresql`; Compose intentionally mounts that parent path. The database volume survives ordinary `docker compose down`. `docker compose down --volumes` permanently removes this local data and the authentication key volume.

## Optional OpenAI summaries

Core incident functionality starts with AI disabled. To enable local draft generation, edit the untracked `.env`:

```dotenv
OPENAI_ENABLED=true
OPENAI_API_KEY=your-own-provider-key
OPENAI_MODEL=gpt-4.1-mini
```

Then recreate the application container:

```sh
docker compose up --detach --force-recreate api
```

The engineer must explicitly request generation, review the returned draft and save it through the normal summary endpoint with the returned source ETag. No external provider is called on incident creation, ordinary reads, status transitions or manual summary saves. Drafting requires at least one note, accepts at most 100 notes and 40,000 total source characters, and is limited to five requests per engineer per minute. Generated content can be inaccurate; source notes and the resolution record remain authoritative.

The default timeout is 30 seconds with no automatic retries. Set `OpenAI__TimeoutSeconds` to an integer from 1 to 120 in the API process environment to change it; for Compose, add that environment setting through a local Compose override. Disabled or incomplete provider configuration returns `503`; an upstream failure or unusable response returns `502`; timeout returns `504`. Core incident management and manual summary saves remain available.

The integration sends incident content to the configured provider. Use synthetic demo data unless the relevant organization has approved that transfer. Keep the API key in local environment configuration or the deployment's secret store. Provider availability, supported models and billing belong to the configured account. The implementation does not silently switch to another provider.

Requests use the OpenAI Responses API with `store: false`, no tools, and a bounded output. Disabling stored responses is not a guarantee of zero provider retention; separate abuse-monitoring and account data controls may apply. See [OpenAI's data controls](https://developers.openai.com/api/docs/guides/your-data).

## Verification and troubleshooting

```sh
docker compose ps --all
docker compose logs migrate
docker compose logs api
curl --fail http://localhost:8080/health/live
curl --fail http://localhost:8080/health/ready
python3 scripts/smoke.py
```

The smoke script creates a uniquely named incident and walks it through reporting, assignment, investigation, a shared note, a reviewed summary and resolution. It checks authentication, reporter isolation, stale/missing version errors and persisted history. It intentionally leaves the resulting incident in the local database for inspection and can be run repeatedly.

| Symptom | Check |
| --- | --- |
| Cannot connect to Docker | Start Docker Desktop or the Docker engine before Compose or integration tests. |
| Port is already allocated | Change `API_PORT`/`POSTGRES_PORT` in `.env`, or stop the other local process. |
| Migration container fails | Read `docker compose logs migrate`; verify the connection and database credentials. The API is blocked until migration succeeds. |
| Database password changes have no effect | The official PostgreSQL image initializes credentials only for an empty data directory. Change the existing database role's password deliberately, or reset disposable local volumes. |
| API runs but readiness fails | Check PostgreSQL health, the application's connection string and unapplied migrations. |
| Tokens stop working after a reset | Sign in again; deleting the key volume invalidates protected tokens. |
| Write returns `412` | Fetch the latest incident and review the conflicting change before retrying. |
| Draft endpoint reports unavailable | Verify `OPENAI_ENABLED`, the key and model, then recreate the API container. Manual summaries still work. |
| Integration tests cannot find Docker | Verify `docker info` works in the same terminal/session as `dotnet test`. |

The GitHub workflow requires no repository secrets for tests or Docker smoke checks. It uses fake AI output in integration tests and keeps OpenAI disabled in Compose. It uploads TRX results even when tests fail. A successful local test run is separate from a successful GitHub Actions run after pushing.

## Send the repository to someone else

The recipient needs the hidden `.git` directory to preserve the existing commits. A GitHub source zip or `git archive` does not include it. The provided packaging script copies only committed files into a fresh transport clone, removes remotes and machine-local Git state, validates identity and packages the portable repository.

1. Confirm the intended files are committed and `git status` is clean.
2. Run `python3 scripts/package-handoff.py --output ../IncidentDesk-handoff.zip`.
3. Share that ZIP. Do not add `.env`, API keys, personal tokens, database dumps or Docker volumes.
4. The recipient extracts `IncidentDesk`, preserving hidden files, and runs `git status`, `git log --oneline` and `git fsck --full`.
5. The recipient follows the README to verify the demo, then creates an **empty** GitHub repository without an initial README, license or `.gitignore`.
6. From inside the extracted project, the recipient runs:

```sh
git remote add origin https://github.com/OWNER/REPOSITORY.git
git push --set-upstream origin main
```

Replace `OWNER/REPOSITORY` with the destination. The recipient authenticates with their own authorized GitHub session or credentials. Do not put an access token in the remote URL or share it in the ZIP. There is no need to reinitialize Git or squash the existing commits.

All supplied commits use `Yosshmi <yosshmi04@gmail.com>` as both author and committer, with actual creation dates. The sanitized repository retains that local identity for later commits. These values are metadata, not a GitHub login; commit attribution on GitHub depends on the destination account's email association. The script checks identities and Git integrity but cannot determine whether arbitrary text in committed history contains a secret; review source and history before sharing.

The packaging script never pushes or changes the source repository. It also verifies a fresh extraction of its ZIP has the same HEAD, clean tracked files, valid objects, the required identities and no configured remote.

## Before a real deployment

The supplied Compose stack is for local review. Production hosting still requires TLS, an appropriate account lifecycle/identity provider, protected persistent Data Protection keys, managed secrets, database backups with restore verification, monitoring and a reviewed migration process. Multi-instance rate limits, enterprise access control and operational retention policies are outside this version. Azure deployment and a React dashboard are intentionally deferred.
