# Website analytics deployment

Adds anonymous, consented public-website event reporting at `/api/admin/website-analytics`.
Existing app endpoints and contracts are unchanged. No app release is needed.

## Configuration before deployment

Generate a random secret of at least 32 characters (for example `openssl rand -hex 32`).
Set the same value in the backend container's `WebsiteAnalytics__IngestKey` and the
Next.js server's `BANTERA_ANALYTICS_INGEST_KEY` environment variables. Do not prefix
this website variable with `NEXT_PUBLIC_`, commit it, or put it in browser code.
The backend setting defaults to empty: ingestion returns 503 until configured, but
existing app features and admin authentication continue to work.

1. Take the usual database backup and deploy the backend first.
2. Startup applies additive migration `20260928220203_AddWebsiteAnalytics`:
   one independent `website_events` table and two indexes; no existing rows are changed.
3. Deploy the website with the matching key. Open `/dashboard/website`; verify that
   there is no configuration or backend-unavailable banner.
4. In a fresh browser tab open `/learn/spanish?utm_source=release-check&utm_medium=test&utm_campaign=analytics-launch`.
   Allow analytics. Open a lesson, play it for 30 seconds, and click a download link.
   Verify source, language, paths and actions in the admin report. An APK/App Store
   click is download intent, not evidence of installation.
5. Decline analytics and confirm subsequent actions send no collection requests.
   Confirm private dashboard routes never generate analytics events.

The daily cleanup removes records older than 90 days (up to a day of cleanup delay).
An additive deploy remains compatible with old mobile apps. Runtime smoke tests
above are required after deployment; local builds do not prove production collection.
Rolling back this feature does not require dropping its table.

## Collection limits

The browser collects only after explicit consent, respects DNT/GPC, and uses a tab
session with a 30-minute inactivity timeout and a 24-hour maximum. No account linkage,
IP storage, recordings, full referrers, search text or arbitrary event payloads.
Campaign tokens are limited to 80 ASCII letters/digits/dots/underscores/hyphens.
Only approved public paths and event names are accepted. Event IDs are idempotent.
Ingestion accepts at most 10 events / 16 KiB and 1,200 requests/minute globally.
Reports query a maximum 90-day range and bound high-cardinality tables to 50 rows,
languages to 80, and the recent event log to 100. Metrics are best-effort client
telemetry, not exhaustive server access logs or search ranking data. Tags/referrers
are reported evidence and can be forged; missing attribution is Direct / unknown.
Consent and blockers mean sessions are not a census of all visitors. Distinct sessions
are not distinct people. Raw events expire; this release has no long-term rollups.

## Local database verification

Create a disposable local database with `analytics_verify` in its name. Apply migrations
with `ConnectionStrings__Postgres` pointing to it. Then run:

```
BANTERA_ANALYTICS_TEST_DB='Host=localhost;Database=bantera_analytics_verify;Username=YOUR_LOCAL_USER' dotnet test BanteraApi.Tests
```

The integration test starts only a local endpoint harness, uses no production credentials,
and verifies ingestion authentication, admin access, idempotency, private-path rejection,
PostgreSQL aggregation and attribution. Without this variable it is explicitly skipped.
