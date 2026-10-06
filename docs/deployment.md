# Public demo deployment

## Neon Free

Create a dedicated Free project in the AWS Singapore region, with a PostgreSQL database used only for fictional IncidentDesk data. Use the direct database endpoint for this single-instance demo: startup migrations use session-scoped database locks. A small Npgsql connection pool is sufficient.

Render requires an Npgsql connection string, rather than the `postgresql://` URL shown by some clients. Set `ConnectionStrings__IncidentDesk` in Render's environment settings, using the values from Neon's connection dialog:

```text
Host=YOUR_DIRECT_NEON_HOST;Port=5432;Database=neondb;Username=YOUR_DATABASE_ROLE;Password=YOUR_DATABASE_PASSWORD;SSL Mode=VerifyFull;Channel Binding=Require;Timeout=30;Command Timeout=60;Maximum Pool Size=10
```

Keep the real password and connection string in Render's secret configuration. Do not commit them. Free plan resource limits and availability are controlled by the providers.

## Render Free

Connect the GitHub repository and create a Blueprint using the root `render.yaml`. The configuration creates one Docker web service on the Free compute plan in Singapore. It does not create a paid database, disk, worker or scheduled job. Supply the Neon connection string when Render asks for `ConnectionStrings__IncidentDesk`.

| Setting | Purpose |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT=Production` | Production exception handling and server defaults |
| `Docs__Enabled=true` | Publish Scalar docs and the OpenAPI schema |
| `Demo__Enabled=true` | Explicitly allow fictional account seeding and show credentials in the docs |
| `INCIDENTDESK_BOOTSTRAP_DEMO=true` | Apply migrations and seed before the container starts the API |
| `DataProtection__PersistToDatabase=true` | Store authentication keys in PostgreSQL instead of the ephemeral filesystem |
| `OpenAI__Enabled=false` | Keep the optional draft integration disabled |

The container entrypoint uses Render's `PORT`, runs the committed migrations and the idempotent seeder, then replaces itself with the API process. A migration or seed failure prevents the server from starting. Local Compose continues to use its separate migration container and persistent filesystem key volume.

The Blueprint checks `/health/ready` and requests automatic deployment only after GitHub checks pass. Initial creation still needs its build and health checks to succeed. Check that the deployed commit is the verified commit. Keep the service on the Free plan.

## Review and verification

After the deployment succeeds, use the actual public HTTPS URL displayed by Render:

```sh
curl --fail https://YOUR_SERVICE.onrender.com/health/live
curl --fail https://YOUR_SERVICE.onrender.com/health/ready
curl --fail https://YOUR_SERVICE.onrender.com/openapi/v1.json
python3 scripts/smoke.py --base-url https://YOUR_SERVICE.onrender.com
```

Open `/docs` and sign in via `POST /api/v1/auth/login`. Copy `accessToken` into the Bearer authentication field in the docs. All four fictional accounts use `IncidentDesk1!`:

| Email | Role |
| --- | --- |
| `reporter1@example.test` | Reporter |
| `reporter2@example.test` | Reporter |
| `engineer1@example.test` | Engineer |
| `engineer2@example.test` | Engineer |

Reporters can create and view their own incidents. Engineers can inspect the queue, assign an engineer and move an incident through `Open → Investigating → Resolved`. Existing-incident writes need the quoted `ETag` from the latest incident read in the `If-Match` request header.

The smoke check leaves a fictional resolved incident for inspection. CI tests the regular local configuration and the explicit Production demo configuration. The integration suite also checks that access and refresh tokens survive replacement application hosts using the database-backed keys.

Use the verified `/docs` URL as the resume's **Live API Demo** link. This project presents a backend API; the interactive docs are its browser interface. Free services sleep after inactivity, so allow time for the first request to wake them. The public demo is shared: visitors can change fictional incidents, and repeated failed login attempts temporarily lock an account.

## Restart and updates

Redeployment reruns migrations and the idempotent seed, preserving existing users and incidents. It does not reset the demo or replace account passwords. Data Protection keys remain in the database across restarts. Deleting those keys invalidates previously issued tokens; deleting the database removes all demo data.

Use a separate database and account configuration for any real incident-management deployment. Disable public demo settings and configure appropriate access, key encryption and backups before using real data.

The interactive docs use the current HTTPS origin. For incident writes, copy the quoted `X-Incident-ETag` response header into `If-Match`. This preserves the original resource version if the hosting CDN weakens the standard `ETag`.
